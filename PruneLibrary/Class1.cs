﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Newtonsoft.Json;

namespace PruneLibrary
{
    public class PruneProcessInstance : IDisposable
    {
        private readonly double _totalCpuThreshold; //number of logical processors

        //Performance counters
        private PerformanceCounter _cpuPc;
        private PerformanceCounter _privBytesPc;
        private PerformanceCounter _workingSetPc;

        //Process name information
        public int ProcessId;
        private Process[] _processesWithName; //array of all processes that share a name with the monitored process
        public string WhitelistEntry;

        //true if library called from Prune Service, false if called from Prune command line tool
        private readonly bool _isService;

        //Set to true after the process's performance counters are set to null and after it logs for the last time.
        public bool ProcessFinished { get; private set; }

        //Intervals in seconds
        private readonly uint _writeCacheInterval; //length of time before writing cache to a file
        private readonly uint _logInterval; //length of time to wait before calculating and logging usage stats

        //file and directory information
        private readonly string _rootDirectory;
        private string _fileSuffix; //Used to create the cache file names

        //collections
        private readonly List<DataPoint> _cache = new List<DataPoint>(); //List of data collected during a single _cache interval
        private readonly List<string> _unloggedFiles = new List<string>(); //Files that have been cached but not been logged yet

        //Threads
        private Thread _loggingThread;

        //Time variables
        private DateTime _cacheStart; //Start of current cache interval
        private DateTime _cacheFinish; //end of current cache interval
        private DateTime _logStart; //start of current stat logging interval
        private DateTime _logFinish = DateTime.MinValue; //end of current stat logging 

        public PruneProcessInstance(bool service, int procId, string whitelistEnt, uint writeInterval, uint logInterval, string root) : this(service, procId, whitelistEnt, writeInterval, logInterval, root, DateTime.MinValue)
        {
            //Empty constructor just used to call the other one    
        }

        /*
         * constructor for the base class  One instance exists for each process that is being monitored
         * @params: Process name, cache write interval, log interval, root directory for service files, and the event log to use
         */
        public PruneProcessInstance(bool service, int procId, string whitelistEnt, uint writeInterval, uint logInterval, string root, DateTime intervalStart)
        {
            ProcessId = procId;
            _writeCacheInterval = writeInterval;
            _logInterval = logInterval;
            _rootDirectory = root;
            _isService = service;
            WhitelistEntry = whitelistEnt;

            //calculate the maximum value for the CPU performance counter, number of logical processor cores times 100.
            _totalCpuThreshold = Environment.ProcessorCount * 100;

            _cacheStart = intervalStart;
            _logStart = intervalStart;
        }

        //Initialize the counters for the current process
        public void InitializeInstance()
        {

            //Set up the performance counters, false is returned if something goes wrong
            SetPerfCounters();

            //set the start and end times 
            if (DateTime.Equals(DateTime.MinValue, _logStart))
            {
                _logStart = DateTime.Now;
            }
            if (DateTime.Equals(DateTime.MinValue, _cacheStart))
            {
                _cacheStart = DateTime.Now;
            }

            //Different interval finishes are needed if this is run from the command line or the service
            if (_isService)
            {
                //From the service, we need to set the log and cache interval end times
                _logFinish = Prune.CalculateIntervalFinishTime(_logStart, _logInterval);
                _cacheFinish = Prune.CalculateIntervalFinishTime(_cacheStart, _writeCacheInterval);
            }
            else
            {
                //From the command line, we don't use the logFinish and cacheFinish is simply the current time plus the length to monitor
                _cacheFinish = DateTime.Now.AddSeconds(_writeCacheInterval);
            }

            //create the current file suffix for use later in the cache files
            _fileSuffix = "-" + _cacheStart.ToString("yyyyMMdd_HHmmss") + "-" + _cacheFinish.ToString("yyyyMMdd_HHmmss") + ".json";
        }

        private void SetPerfCounters()
        {
            string instanceName;

            //get the name of the process from the PID
            string procNameTemp = Prune.GetProcessNameFromProcessId(ProcessId);

            //Get a list of all processes with that name
            _processesWithName = Prune.GetProcessesFromProcessName(procNameTemp);

            //Get the instance name that corresponds to the process id
            try
            {
                instanceName = Prune.GetInstanceNameForProcessId(ProcessId);
            }
            catch (Exception e)
            {
                Prune.HandleError(_isService, "Error getting instance name. Setting it to null\n" + e.Message);
                instanceName = null;
            }

            //For each process, set up an event handler for when a process exits
            //This is because if a process exits, the instance names of other processes with the same name may change
            //We need to use the instance name if we get the PID, so recalculate the correct instance name
            if (_isService)
            {
                try
                {
                    foreach (Process proc in _processesWithName)
                    {
                        proc.EnableRaisingEvents = true;
                        proc.Exited += (sender, e) => { SetPerfCounters(); };
                    }
                }
                catch (Exception)
                {
                    if (_isService)
                    {
                        Prune.EventLog.WriteEntry(
                            "Unable to attach listener to process exit event for process " + WhitelistEntry + "_" +
                            ProcessId +
                            ". If this is part of a multi-process program and any process that shares the name closes, behaviour will be undefined.\n",
                            EventLogEntryType.Warning, 5);
                    }
                }
            }

            //if the string is not null or empty, set up Perf Counters
            if (!String.IsNullOrWhiteSpace(instanceName))
            {
                try
                {
                    //Create the % processor time counter and start it
                    //the first next value call is required to begin gathering information on the process
                    _cpuPc = new PerformanceCounter("Process", "% Processor Time", instanceName, true);
                    _cpuPc.NextValue();
                }
                catch (Exception e)
                {
                    Prune.HandleError(_isService, "Failed to initialize % Processor Time performance counter\n" + e.Message + "\n" + e.InnerException + "\n" + e.GetBaseException());
                    return;
                }

                try
                {
                    //create the private bytes counter and start it
                    _privBytesPc = new PerformanceCounter("Process", "Private Bytes", instanceName, true);
                    _privBytesPc.NextValue();
                }
                catch (Exception e)
                {
                    Prune.HandleError(_isService, "Failed to initialize Private Bytes performance counter\n" + e.Message);
                    return;
                }

                try
                {
                    //create the working set counter and start it
                    _workingSetPc = new PerformanceCounter("Process", "Working Set - Private", instanceName, true);
                    _workingSetPc.NextValue();
                }
                catch (Exception e)
                {
                    Prune.HandleError(_isService, "Failed to initialize Private Working Set performance counter\n" + e.Message);
                    return;
                }

                //give the counters a quarter of a second to ensure they start to gather data
                Thread.Sleep(250);
            }
            else
            {
                //The procName was null or empty, so set them all to null
                NullPerformanceCounters();
            }
        }

        //Set all performance counters to null
        public void NullPerformanceCounters()
        {
            _cpuPc = null;
            _privBytesPc = null;
            _workingSetPc = null;
        }

        //Go through the currently cached data and write it to a file
        public void WriteCacheToFile()
        {
            //Only write contents of the cache to a file if there is something in the cache
            if (_cache.Count > 0)
            {
                //create the full path and file name
                string directory = _rootDirectory + "\\" + WhitelistEntry;

                //If this is being used form the command line tool, the file should go straight into
                //      The logged sub-directory of the process directory
                if (!_isService)
                {
                    directory = directory + "\\logged";
                }

                string fileName = directory + "\\" + WhitelistEntry + "_" + ProcessId + _fileSuffix;


                if (!_isService)
                    Console.WriteLine("\nWriting to file: " + fileName);

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                try
                {


                    //convert the cache list to json format and write
                    string cacheJson = JsonConvert.SerializeObject(_cache, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore });

                    using (StreamWriter sw = new StreamWriter(fileName, false))
                    {
                        sw.Write(cacheJson);
                        sw.Flush();
                    }
                }
                catch (Exception e)
                {
                    Prune.HandleError(_isService, "Error writing cache to file\n" + e.Message);
                }

                //Only add the file to the unlogged file list if it was generated by the service
                //Additional code also only needed for service, as the command line tool ends after writing first file
                if (_isService)
                {
                    //add this file to the list of unlogged files
                    lock (_unloggedFiles)
                    {
                        _unloggedFiles.Add(fileName);
                    }

                    //recreate the file suffix for the next file
                    _fileSuffix = "-" + _cacheStart.ToString("yyyyMMdd_HHmmss") + "-" +
                                 _cacheFinish.ToString("yyyyMMdd_HHmmss") + ".json";

                    //clear the cache list for future use
                    _cache.Clear();
                }
            }

            //Only log from files if this is running from the service
            if (_isService)
            {
                //after the cache has been cleared, check to see if the logging interval has passed
                //  And verify that there is a file that needs logging
                if (DateTime.Compare(_logFinish, DateTime.MinValue) > 0 &&
                    DateTime.Compare(_logFinish, DateTime.Now) < 0 && _unloggedFiles.Count > 0)
                {
                    //if it has, launch a thread to process the information from the files and log the results
                    try
                    {
                        _loggingThread = new Thread(LogDataFromCacheFiles);
                        _loggingThread.Start();
                    }
                    catch (Exception e)
                    {
                        Prune.HandleError(_isService, "Failed to start thread to log usage statistics\n" + e.Message);
                    }
                }
            }
        }

        //read in data from the cache files and process it
        //  output is an event in the event log
        public void LogDataFromCacheFiles()
        {
            List<string> unloggedCopy;

            lock (_unloggedFiles)
            {
                //make a copy of the unlogged files list and then clear the original so more entries can be added
                unloggedCopy = new List<string>(_unloggedFiles);
                _unloggedFiles.Clear();
            }

            LogDataFromFiles(unloggedCopy, false);
        }

        public void LogDataFromFiles(List<string> list, bool loggingFromOldFiles)
        {

            //the number of data points processed
            uint dataPointCount = 0;

            //variables to hold calculations
            double averageCpu = 0;
            double maxCpu = double.MinValue;
            double minCpu = double.MaxValue;

            long averagePriv = 0;
            long maxPriv = long.MinValue;
            long minPriv = long.MaxValue;

            long averageWorking = 0;
            long maxWorking = long.MinValue;
            long minWorking = long.MaxValue;

            long averageTcpOut = 0;
            long maxTcpOut = long.MinValue;
            long minTcpOut = long.MaxValue;

            long averageTcpIn = 0;
            long maxTcpIn = long.MinValue;
            long minTcpIn = long.MaxValue;

            long averageUdpOut = 0;
            long maxUdpOut = long.MinValue;
            long minUdpOut = long.MaxValue;

            long averageUdpIn = 0;
            long maxUdpIn = long.MinValue;
            long minUdpIn = long.MaxValue;

            long averageDiskReadBytes = 0;
            long maxDiskReadBytes = long.MinValue;
            long minDiskReadBytes = long.MaxValue;

            long averageDiskWriteBytes = 0;
            long maxDiskWriteBytes = long.MinValue;
            long minDiskWriteBytes = long.MaxValue;

            long averageDiskReadOps = 0;
            long maxDiskReadOps = long.MinValue;
            long minDiskReadOps = long.MaxValue;

            long averageDiskWriteOps = 0;
            long maxDiskWriteOps = long.MinValue;
            long minDiskWriteOps = long.MaxValue;

            Dictionary<string, TcpConnectionData> connectionData = new Dictionary<string, TcpConnectionData>();

            if (list == null || list.Count == 0)
            {
                return;
            }

            //loop through all unlogged files
            foreach (string fileName in list)
            {
                List<DataPoint> fileList = new List<DataPoint>();

                try
                {
                    //parse file into an object
                    string fileText = File.ReadAllText(fileName);

                    //Doing this the hard way because the default deserialization methods produce null value errors for some reason
                    //Deserailize the array
                    dynamic o = JsonConvert.DeserializeObject(fileText);

                    //Loop through each array element
                    foreach (Newtonsoft.Json.Linq.JObject obj in (Newtonsoft.Json.Linq.JArray)o)
                    {
                        //Create a new DataPoint from each property of the object, each of which must be converted to the correct type with ToObject<>()
                        fileList.Add(new DataPoint(obj["CpuVal"].ToObject<double>(), obj["PrivBytesVal"].ToObject<long>(), obj["WorkingBytesVal"].ToObject<long>(),
                            obj["DiskBytesReadVal"].ToObject<long>(), obj["DiskBytesWriteVal"].ToObject<long>(), obj["DiskOpsReadVal"].ToObject<long>(),
                            obj["DiskOpsWriteVal"].ToObject<long>(), obj["UdpSent"].ToObject<long>(),
                            obj["UdpRecv"].ToObject<long>(), obj["TcpSent"].ToObject<long>(), obj["TcpRecv"].ToObject<long>(), obj["ConnectionsSent"].ToObject<Dictionary<string, long>>(),
                            obj["ConnectionsReceived"].ToObject<Dictionary<string, long>>(), obj["LogTime"].ToObject<DateTime>())
                        );
                    }
                }
                catch (Exception e)
                {
                    Prune.HandleError(_isService, "Failed to read file " + fileName + " and deserialize it\n" + e.Message);
                    return;
                }

                //loop through the array doing the math as normal
                //increment dataPointCount in the dataPoint loop
                foreach (DataPoint data in fileList)
                {
                    dataPointCount++;

                    double cpu = data.CpuVal;
                    long priv = data.PrivBytesVal;
                    long work = data.WorkingBytesVal;
                    long tcpIn = data.TcpRecv;
                    long tcpOut = data.TcpSent;
                    long udpIn = data.UdpRecv;
                    long udpOut = data.UdpSent;
                    long diskReadBytes = data.DiskBytesReadVal;
                    long diskWriteBytes = data.DiskBytesWriteVal;
                    long diskReadOps = data.DiskOpsReadVal;
                    long diskWriteOps = data.DiskOpsWriteVal;

                    averageCpu += cpu;
                    averagePriv += priv;
                    averageWorking += work;
                    averageTcpIn += tcpIn;
                    averageTcpOut += tcpOut;
                    averageUdpIn += udpIn;
                    averageUdpOut += udpOut;
                    averageDiskReadBytes += diskReadBytes;
                    averageDiskWriteBytes += diskWriteBytes;
                    averageDiskReadOps += diskReadOps;
                    averageDiskWriteOps += diskWriteOps;

                    if (cpu > maxCpu)
                    {
                        maxCpu = cpu;
                    }
                    if (cpu < minCpu)
                    {
                        minCpu = cpu;
                    }

                    if (priv > maxPriv)
                    {
                        maxPriv = priv;
                    }
                    if (priv < minPriv)
                    {
                        minPriv = priv;
                    }

                    if (work > maxWorking)
                    {
                        maxWorking = work;
                    }
                    if (work < minWorking)
                    {
                        minWorking = work;
                    }

                    if (tcpIn > maxTcpIn)
                    {
                        maxTcpIn = tcpIn;
                    }
                    if (tcpIn < minTcpIn)
                    {
                        minTcpIn = tcpIn;
                    }

                    if (tcpOut > maxTcpOut)
                    {
                        maxTcpOut = tcpOut;
                    }
                    if (tcpOut < minTcpOut)
                    {
                        minTcpOut = tcpOut;
                    }

                    if (udpIn > maxUdpIn)
                    {
                        maxUdpIn = udpIn;
                    }
                    if (udpIn < minUdpIn)
                    {
                        minUdpIn = udpIn;
                    }

                    if (udpOut > maxUdpOut)
                    {
                        maxUdpOut = udpOut;
                    }
                    if (udpOut < minUdpOut)
                    {
                        minUdpOut = udpOut;
                    }

                    if (diskReadBytes > maxDiskReadBytes)
                    {
                        maxDiskReadBytes = diskReadBytes;
                    }
                    if (diskReadBytes < minDiskReadBytes)
                    {
                        minDiskReadBytes = diskReadBytes;
                    }

                    if (diskWriteBytes > maxDiskWriteBytes)
                    {
                        maxDiskWriteBytes = diskWriteBytes;
                    }
                    if (diskWriteBytes < minDiskWriteBytes)
                    {
                        minDiskWriteBytes = diskWriteBytes;
                    }

                    if (diskReadOps > maxDiskReadOps)
                    {
                        maxDiskReadOps = diskReadOps;
                    }
                    if (diskReadOps < minDiskReadOps)
                    {
                        minDiskReadOps = diskReadOps;
                    }

                    if (diskWriteOps > maxDiskWriteOps)
                    {
                        maxDiskWriteOps = diskWriteOps;
                    }
                    if (diskWriteOps < minDiskWriteOps)
                    {
                        minDiskWriteOps = diskWriteOps;
                    }

                    foreach (string key in data.ConnectionsSent.Keys)
                    {
                        if (!connectionData.ContainsKey(key))
                        {
                            connectionData.Add(key, new TcpConnectionData(key, _isService, Prune.GetProcessNameFromProcessId(ProcessId)));
                        }

                        connectionData[key].AddOutData(data.ConnectionsSent[key]);
                    }

                    foreach (string key in data.ConnectionsReceived.Keys)
                    {
                        if (!connectionData.ContainsKey(key))
                        {
                            connectionData.Add(key, new TcpConnectionData(key, _isService, Prune.GetProcessNameFromProcessId(ProcessId)));
                        }

                        connectionData[key].AddInData(data.ConnectionsReceived[key]);
                    }
                }

                string[] fileNameSplit = fileName.Split('\\');
                string loggedDirectory = _rootDirectory + "\\" + WhitelistEntry + "\\logged\\";

                try
                {
                    //Move the file, that has been completely processed, into the logged subfolder
                    Directory.CreateDirectory(loggedDirectory);

                    //Move the file to the logged directory
                    File.Move(fileName, loggedDirectory + fileNameSplit[fileNameSplit.Length - 1]);
                }
                catch (Exception e)
                {
                    Prune.HandleError(_isService, "Failed to move file " + fileName + " to " + loggedDirectory + fileNameSplit[fileNameSplit.Length - 1] + " after logging\n" + e.Message);
                }
            }

            //Get a few total for the I/O related measures
            long totalTcpIn = averageTcpIn;
            long totalTcpOut = averageTcpOut;
            long totalUdpIn = averageUdpIn;
            long totalUdpOut = averageUdpOut;
            long totalDiskReadBytes = averageDiskReadBytes;
            long totalDiskWriteBytes = averageDiskWriteBytes;
            long totalDiskReadOps = averageDiskReadOps;
            long totalDiskWriteOps = averageDiskWriteOps;

            //divide the sums to get averages
            averageCpu /= dataPointCount;
            averagePriv /= dataPointCount;
            averageWorking /= dataPointCount;
            averageTcpIn /= dataPointCount;
            averageTcpOut /= dataPointCount;
            averageUdpIn /= dataPointCount;
            averageUdpOut /= dataPointCount;
            averageDiskReadBytes /= dataPointCount;
            averageDiskReadOps /= dataPointCount;
            averageDiskWriteBytes /= dataPointCount;
            averageDiskWriteOps /= dataPointCount;

            try
            {
                string outputString = WhitelistEntry + "_" + ProcessId + " -- # of Data Points Collected: " + dataPointCount +
                "\n\n% CPU Usage\n" + "Average: " + averageCpu.ToString("N2") + "; Maximum: " + maxCpu.ToString("N2") + "; Minimum: " + minCpu.ToString("N2") +
                "\n\nPrivate Bytes\n" + "Average: " + averagePriv.ToString("N0") + "; Maximum: " + maxPriv.ToString("N0") + "; Minimum: " + minPriv.ToString("N0") +
                "\n\nPrivate Working Set\n" + "Average: " + averageWorking.ToString("N0") + "; Maximum: " + maxWorking.ToString("N0") + "; Minimum: " + minWorking.ToString("N0") +
                "\n\nDisk Read Operations\nTotal: " + totalDiskReadOps.ToString("N0") + "; Average: " + averageDiskReadOps.ToString("N0") + "; Maximum: " + maxDiskReadOps.ToString("N0") + "; Minimum: " + minDiskReadOps.ToString("N0") +
                "\n\nDisk Write Operations\nTotal: " + totalDiskWriteOps.ToString("N0") + "; Average: " + averageDiskWriteOps.ToString("N0") + "; Maximum: " + maxDiskWriteOps.ToString("N0") + "; Minimum: " + minDiskWriteOps.ToString("N0") +
                "\n\nDisk Read Bytes\nTotal: " + totalDiskReadBytes.ToString("N0") + "; Average: " + averageDiskReadBytes.ToString("N0") + "; Maximum: " + maxDiskReadBytes.ToString("N0") + "; Minimum: " + minDiskReadBytes.ToString("N0") +
                "\n\nDisk Write Bytes\nTotal: " + totalDiskWriteBytes.ToString("N0") + "; Average: " + averageDiskWriteBytes.ToString("N0") + "; Maximum: " + maxDiskWriteBytes.ToString("N0") + "; Minimum: " + minDiskWriteBytes.ToString("N0") +
                "\n\nProcess TCP Bytes Sent\nTotal: " + totalTcpOut.ToString("N0") + "; Average: " + averageTcpOut.ToString("N0") + "; Maximum: " + maxTcpOut.ToString("N0") + "; Minimum: " + minTcpOut.ToString("N0") +
                "\n\nProcess TCP Bytes Received\nTotal: " + totalTcpIn.ToString("N0") + "; Average: " + averageTcpIn.ToString("N0") + "; Maximum: " + maxTcpIn.ToString("N0") + "; Minimum: " + minTcpIn.ToString("N0") +
                "\n\nProcess UDP Bytes Sent\nTotal: " + totalUdpOut.ToString("N0") + "; Average:" + averageUdpOut.ToString("N0") + "; Maximum: " + maxUdpOut.ToString("N0") + "; Minimum: " + minUdpOut.ToString("N0") +
                "\n\nProcess UDP Bytes Received\nTotal: " + totalUdpIn.ToString("N0") + "; Average:" + averageUdpIn.ToString("N0") + "; Maximum: " + maxUdpIn.ToString("N0") + "; Minimum: " + minUdpIn.ToString("N0") +
                "\n\nSpecific Connection Statistics";

                //Add the information from each individual connection
                foreach (TcpConnectionData data in connectionData.Values)
                {
                    data.CalculateStats();

                    outputString += "\n\n" + data.HostName + " -- " + data.Address;
                    outputString += "\nTotal Tcp Bytes Sent: " + data.TotalOut.ToString("N0") + "; Average TCP Bytes Sent: " + data.AverageOut.ToString("N0") + "; Maximum TCP Bytes Sent: " + data.MaxOut.ToString("N0") + "; Minimum TCP Bytes Sent: " + data.MinOut.ToString("N0");
                    outputString += "\nTotal Tcp Bytes Sent: " + data.TotalIn.ToString("N0") + "; Average TCP Bytes Received: " + data.AverageIn.ToString("N0") + "; Maximum TCP Bytes Received: " + data.MaxIn.ToString("N0") + "; Minimum TCP Bytes Received: " + data.MinIn.ToString("N0");

                }

                //write the event
                Prune.EventLog.WriteEntry(outputString, EventLogEntryType.Information, 10);
            }
            catch (Exception e)
            {
                Prune.HandleError(_isService, "Error outputting statistics to log\n" + e.Message);
            }

            if (_cpuPc == null && _privBytesPc == null && _workingSetPc == null)
            {
                ProcessFinished = true;
            }

            //If this is not logging files left over from a previous running of the tool, then advance the log interval times
            if (!loggingFromOldFiles)
            {
                _logStart = _logFinish;
                _logFinish = _logStart.AddSeconds(_logInterval);
            }
        }

        public void FinishMonitoring()
        {
            Prune.EventLog.WriteEntry("Finishing monitoring for " + WhitelistEntry + "_" + ProcessId, EventLogEntryType.Information, 3);
            NullPerformanceCounters();
            DumpCache();
            LogDataFromCacheFiles();
        }

        //The service is stopping, so we need to dump the current cache to a file
        public void DumpCache()
        {
            //Create file name and full path
            string fileName = _rootDirectory + "\\" + WhitelistEntry + "\\" + WhitelistEntry + "_" + ProcessId + "-" + _cacheStart.ToString("yyyyMMdd_HHmmss") + "-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json";

            //Create the process directory if it does not already exist
            Directory.CreateDirectory(_rootDirectory + "\\" + WhitelistEntry + "\\");

            try
            {
                //convert list to json
                string cacheJson = JsonConvert.SerializeObject(_cache);

                //write json to the file
                using (StreamWriter sw = new StreamWriter(fileName))
                {
                    sw.Write(cacheJson);
                    sw.Flush();
                }

                _unloggedFiles.Add(fileName);
            }
            catch (Exception e)
            {
                Prune.HandleError(_isService, "Failed to dump cache to " + fileName + " on service shutdown\n" + e.Message);
            }
        }

        //Record the next data point from each counter and add it to the cache list
        public void GetData()
        {
            //If the current cache interval is over
            if (_isService && DateTime.Compare(_cacheFinish, DateTime.Now) < 0)
            {
                //adjust the cache times to the next interval
                _cacheStart = _cacheFinish;
                _cacheFinish = _cacheStart.AddSeconds(_writeCacheInterval);

                //write the current cache to a file before adding new information to the cache
                WriteCacheToFile();

            }

            //add the data inside of a DataPoint object
            //The Cpu value is divided by the total cpu threshold to convert it to a percent out of 100, where 100% is total utilization of all processor cores
            //Only do this if all of the Perf Counters are initialized properly
            if (_cpuPc != null && _privBytesPc != null && _workingSetPc != null)
            {
                double cpuAdjusted;
                double privValue;
                double workingValue;

                try
                {
                    cpuAdjusted = (_cpuPc.NextValue() / _totalCpuThreshold) * 100;
                    privValue = _privBytesPc.NextValue();
                    workingValue = _workingSetPc.NextValue();
                }
                catch (Exception)
                {
                    if (_isService)
                    {
                        Prune.EventLog.WriteEntry(
                            "Information can no longer be gathered for process " + WhitelistEntry + "_" + ProcessId +
                            ". The most common reason for this is the process ending.", EventLogEntryType.Information, 4);
                    }

                    NullPerformanceCounters();
                    return;
                }

                //reset localEtwCounters
                Prune.Counters localEtwCounters = null;

                //Get the ETW data, which includes Disk I/O and Network I/O
                try
                {
                    localEtwCounters = Prune.GetEtwDataForProcess(ProcessId);
                }
                catch (Exception e)
                {
                    Prune.HandleError(_isService, "Error getting ETW Data:\n" + e.Message);
                }

                //If nothing is retrieved, create a new Counter object with all values set to 0
                if (localEtwCounters == null)
                {
                    localEtwCounters = new Prune.Counters();
                }

                //Add the data point to the cache
                DataPoint tempDataPoint = new DataPoint(cpuAdjusted, Convert.ToInt64(privValue), Convert.ToInt64(workingValue), localEtwCounters.DiskReadBytes, localEtwCounters.DiskWriteBytes,
                        localEtwCounters.DiskReadOperations, localEtwCounters.DiskWriteOperations, localEtwCounters.UdpSent, localEtwCounters.UdpReceived,
                        localEtwCounters.TcpSent, localEtwCounters.TcpReceived, localEtwCounters.ConnectionsSent, localEtwCounters.ConnectionsReceived, DateTime.Now);
                _cache.Add(tempDataPoint);
            }
        }

        //Adds a file to the unlogged files list
        public void AddUnloggedCacheFile(string fileName)
        {
            _unloggedFiles.Add(fileName);
        }

        public void AddUnloggedCacheFiles(List<string> list)
        {
            foreach (string fileName in list)
            {
                AddUnloggedCacheFile(fileName);
            }
        }

        #region IDisposable Support
        private bool _disposedValue; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _cpuPc.Dispose();
                    _privBytesPc.Dispose();
                    _workingSetPc.Dispose();
                }

                _disposedValue = true;
            }
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
        }
        #endregion
    }
}