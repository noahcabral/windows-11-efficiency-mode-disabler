using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

namespace DcaEfficiencyModeService
{
    internal static class Program
    {
        private const int DefaultIntervalMs = 5000;

        private static int Main(string[] args)
        {
            ServiceOptions options = ServiceOptions.Parse(args);
            EfficiencyModeWorker worker = new EfficiencyModeWorker(options.IntervalMs);

            if (options.Once)
            {
                NativeMethods.EnableDebugPrivilege();
                ScanResult result = worker.ScanOnce(options.DryRun);
                Console.WriteLine(result.ToLogLine(options.DryRun));
                return 0;
            }

            if (options.ConsoleMode)
            {
                NativeMethods.EnableDebugPrivilege();
                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs eventArgs)
                {
                    eventArgs.Cancel = true;
                    worker.Stop();
                };
                worker.RunConsole(options.DryRun);
                return 0;
            }

            ServiceBase.Run(new EfficiencyModeWindowsService(DefaultIntervalMs));
            return 0;
        }
    }

    internal sealed class EfficiencyModeWindowsService : ServiceBase
    {
        private readonly EfficiencyModeWorker _worker;

        public EfficiencyModeWindowsService(int intervalMs)
        {
            ServiceName = "DcaEfficiencyModeDisabler";
            CanStop = true;
            CanPauseAndContinue = false;
            AutoLog = true;
            _worker = new EfficiencyModeWorker(intervalMs);
        }

        protected override void OnStart(string[] args)
        {
            NativeMethods.EnableDebugPrivilege();
            _worker.Start(false);
        }

        protected override void OnStop()
        {
            _worker.Stop();
        }
    }

    internal sealed class EfficiencyModeWorker
    {
        private readonly int _intervalMs;
        private readonly ManualResetEvent _stopEvent = new ManualResetEvent(false);
        private Thread _thread;
        private DateTime _lastQuietLogUtc = DateTime.MinValue;

        public EfficiencyModeWorker(int intervalMs)
        {
            _intervalMs = intervalMs < 1000 ? 1000 : intervalMs;
        }

        public void Start(bool dryRun)
        {
            if (_thread != null)
            {
                return;
            }

            _stopEvent.Reset();
            _thread = new Thread(delegate() { RunLoop(dryRun); });
            _thread.IsBackground = true;
            _thread.Name = "DCA Efficiency Mode Worker";
            _thread.Start();
        }

        public void RunConsole(bool dryRun)
        {
            Log("console worker started dry_run=" + dryRun);
            RunLoop(dryRun);
        }

        public void Stop()
        {
            _stopEvent.Set();
            if (_thread != null && !_thread.Join(5000))
            {
                Log("worker did not stop within timeout");
            }
        }

        private void RunLoop(bool dryRun)
        {
            Log("worker started dry_run=" + dryRun + " interval_ms=" + _intervalMs);
            while (!_stopEvent.WaitOne(0))
            {
                try
                {
                    ScanResult result = ScanOnce(dryRun);
                    bool changed = result.ProcessesCleared > 0 || result.PriorityClassesRestored > 0;
                    bool quietLogDue = (DateTime.UtcNow - _lastQuietLogUtc).TotalMinutes >= 5.0;
                    if (changed || quietLogDue)
                    {
                        Log(result.ToLogLine(dryRun));
                        if (!changed)
                        {
                            _lastQuietLogUtc = DateTime.UtcNow;
                        }
                    }
                }
                catch (Exception exc)
                {
                    Log("scan failed: " + exc.GetType().Name + ": " + exc.Message);
                }

                _stopEvent.WaitOne(_intervalMs);
            }
            Log("worker stopped");
        }

        public ScanResult ScanOnce(bool dryRun)
        {
            ScanResult result = new ScanResult();
            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
            }
            catch
            {
                result.ProcessEnumerationFailures++;
                return result;
            }

            foreach (Process process in processes)
            {
                result.ProcessesSeen++;
                int pid;
                try
                {
                    pid = process.Id;
                }
                catch
                {
                    result.ProcessQueryFailures++;
                    continue;
                }

                if (pid <= 0)
                {
                    continue;
                }

                IntPtr processHandle = NativeMethods.OpenProcess(
                    NativeMethods.ProcessQueryLimitedInformation | NativeMethods.ProcessSetInformation,
                    false,
                    pid);
                if (processHandle == IntPtr.Zero)
                {
                    result.ProcessOpenFailures++;
                    continue;
                }

                try
                {
                    PowerThrottlingState state;
                    if (NativeMethods.TryGetProcessPowerThrottling(processHandle, out state))
                    {
                        result.ProcessesReadable++;
                        if (NativeMethods.HasExecutionSpeedThrottling(state))
                        {
                            result.ProcessesEcoQos++;
                            if (!dryRun && NativeMethods.TrySetProcessHighQos(processHandle))
                            {
                                result.ProcessesCleared++;
                            }
                            else if (!dryRun)
                            {
                                result.ProcessSetFailures++;
                            }
                        }
                    }
                    else
                    {
                        result.ProcessQueryFailures++;
                    }

                    RestoreIdlePriorityClass(processHandle, dryRun, result);
                    ClearEfficientThreads(process, dryRun, result);
                }
                finally
                {
                    NativeMethods.CloseHandle(processHandle);
                    process.Dispose();
                }
            }

            return result;
        }

        private static void RestoreIdlePriorityClass(IntPtr processHandle, bool dryRun, ScanResult result)
        {
            uint priorityClass = NativeMethods.GetProcessPriorityClass(processHandle);
            if (priorityClass == 0)
            {
                result.PriorityClassQueryFailures++;
                return;
            }

            result.PriorityClassesReadable++;
            if (priorityClass != NativeMethods.IdlePriorityClass)
            {
                return;
            }

            result.IdlePriorityClasses++;
            if (dryRun)
            {
                return;
            }

            if (NativeMethods.TrySetNormalPriorityClass(processHandle))
            {
                result.PriorityClassesRestored++;
            }
            else
            {
                result.PriorityClassSetFailures++;
            }
        }

        private static void ClearEfficientThreads(
            Process process,
            bool dryRun,
            ScanResult result)
        {
            ProcessThreadCollection threads;
            try
            {
                threads = process.Threads;
            }
            catch
            {
                result.ThreadEnumerationFailures++;
                return;
            }

            foreach (ProcessThread thread in threads)
            {
                result.ThreadsSeen++;
                IntPtr threadHandle = IntPtr.Zero;
                try
                {
                    threadHandle = NativeMethods.OpenThread(
                        NativeMethods.ThreadSetInformation | NativeMethods.ThreadSetLimitedInformation,
                        false,
                        thread.Id);
                    if (threadHandle == IntPtr.Zero)
                    {
                        result.ThreadOpenFailures++;
                        continue;
                    }

                    result.ThreadsOpened++;
                    if (!dryRun && NativeMethods.TrySetThreadHighQos(threadHandle))
                    {
                        result.ThreadsHighQosApplied++;
                    }
                    else if (!dryRun)
                    {
                        result.ThreadSetFailures++;
                    }
                }
                catch
                {
                    result.ThreadSetFailures++;
                }
                finally
                {
                    if (threadHandle != IntPtr.Zero)
                    {
                        NativeMethods.CloseHandle(threadHandle);
                    }
                    thread.Dispose();
                }
            }
        }

        private static void Log(string message)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string path = Path.Combine(baseDir, "DcaEfficiencyModeService.log");
                RotateLogIfNeeded(path);
                File.AppendAllText(path, DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine);
            }
            catch
            {
            }
        }

        private static void RotateLogIfNeeded(string path)
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length < 2 * 1024 * 1024)
            {
                return;
            }

            string oldPath = path + ".old";
            if (File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }
            File.Move(path, oldPath);
        }
    }

    internal sealed class ScanResult
    {
        public int ProcessesSeen;
        public int ProcessesReadable;
        public int ProcessesEcoQos;
        public int ProcessesCleared;
        public int PriorityClassesReadable;
        public int IdlePriorityClasses;
        public int PriorityClassesRestored;
        public int PriorityClassQueryFailures;
        public int PriorityClassSetFailures;
        public int ProcessOpenFailures;
        public int ProcessQueryFailures;
        public int ProcessSetFailures;
        public int ProcessEnumerationFailures;
        public int ThreadsSeen;
        public int ThreadsOpened;
        public int ThreadsHighQosApplied;
        public int ThreadOpenFailures;
        public int ThreadSetFailures;
        public int ThreadEnumerationFailures;

        public string ToLogLine(bool dryRun)
        {
            return string.Format(
                "scan dry_run={0} processes_seen={1} processes_readable={2} processes_ecoqos={3} processes_cleared={4} priority_readable={5} idle_priority={6} priority_restored={7} priority_query_failures={8} priority_set_failures={9} process_open_failures={10} process_query_failures={11} process_set_failures={12} process_enum_failures={13} threads_seen={14} threads_opened={15} threads_highqos_applied={16} thread_open_failures={17} thread_set_failures={18} thread_enum_failures={19}",
                dryRun,
                ProcessesSeen,
                ProcessesReadable,
                ProcessesEcoQos,
                ProcessesCleared,
                PriorityClassesReadable,
                IdlePriorityClasses,
                PriorityClassesRestored,
                PriorityClassQueryFailures,
                PriorityClassSetFailures,
                ProcessOpenFailures,
                ProcessQueryFailures,
                ProcessSetFailures,
                ProcessEnumerationFailures,
                ThreadsSeen,
                ThreadsOpened,
                ThreadsHighQosApplied,
                ThreadOpenFailures,
                ThreadSetFailures,
                ThreadEnumerationFailures);
        }
    }

    internal sealed class ServiceOptions
    {
        public bool Once;
        public bool DryRun;
        public bool ConsoleMode;
        public int IntervalMs = 5000;

        public static ServiceOptions Parse(string[] args)
        {
            ServiceOptions options = new ServiceOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].Trim();
                if (StringComparer.OrdinalIgnoreCase.Equals(arg, "--once"))
                {
                    options.Once = true;
                }
                else if (StringComparer.OrdinalIgnoreCase.Equals(arg, "--dry-run"))
                {
                    options.DryRun = true;
                }
                else if (StringComparer.OrdinalIgnoreCase.Equals(arg, "--console"))
                {
                    options.ConsoleMode = true;
                }
                else if (StringComparer.OrdinalIgnoreCase.Equals(arg, "--interval-ms") && i + 1 < args.Length)
                {
                    int parsed;
                    if (int.TryParse(args[i + 1], out parsed))
                    {
                        options.IntervalMs = parsed;
                    }
                    i++;
                }
            }
            return options;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    internal static class NativeMethods
    {
        public const uint ProcessSetInformation = 0x0200;
        public const uint ProcessQueryLimitedInformation = 0x1000;
        public const uint ThreadSetInformation = 0x0020;
        public const uint ThreadSetLimitedInformation = 0x0400;
        public const uint IdlePriorityClass = 0x00000040;
        private const uint NormalPriorityClass = 0x00000020;

        private const uint ExecutionSpeed = 0x1;
        private const uint CurrentVersion = 1;
        private const int ProcessPowerThrottling = 4;
        private const int ThreadPowerThrottling = 3;

        private const uint TokenAdjustPrivileges = 0x0020;
        private const uint TokenQuery = 0x0008;
        private const uint SePrivilegeEnabled = 0x00000002;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, int threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessInformation(
            IntPtr process,
            int processInformationClass,
            ref PowerThrottlingState processInformation,
            int processInformationSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessInformation(
            IntPtr process,
            int processInformationClass,
            ref PowerThrottlingState processInformation,
            int processInformationSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetPriorityClass(IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetThreadInformation(
            IntPtr thread,
            int threadInformationClass,
            ref PowerThrottlingState threadInformation,
            int threadInformationSize);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(
            IntPtr processHandle,
            uint desiredAccess,
            out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValue(
            string systemName,
            string name,
            out Luid luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(
            IntPtr tokenHandle,
            bool disableAllPrivileges,
            ref TokenPrivileges newState,
            int bufferLength,
            IntPtr previousState,
            IntPtr returnLength);

        public static bool TryGetProcessPowerThrottling(IntPtr process, out PowerThrottlingState state)
        {
            state = EmptyState();
            return GetProcessInformation(
                process,
                ProcessPowerThrottling,
                ref state,
                Marshal.SizeOf(typeof(PowerThrottlingState)));
        }

        public static bool TrySetProcessHighQos(IntPtr process)
        {
            PowerThrottlingState state = HighQosState();
            return SetProcessInformation(
                process,
                ProcessPowerThrottling,
                ref state,
                Marshal.SizeOf(typeof(PowerThrottlingState)));
        }

        public static uint GetProcessPriorityClass(IntPtr process)
        {
            return GetPriorityClass(process);
        }

        public static bool TrySetNormalPriorityClass(IntPtr process)
        {
            return SetPriorityClass(process, NormalPriorityClass);
        }

        public static bool TrySetThreadHighQos(IntPtr thread)
        {
            PowerThrottlingState state = HighQosState();
            return SetThreadInformation(
                thread,
                ThreadPowerThrottling,
                ref state,
                Marshal.SizeOf(typeof(PowerThrottlingState)));
        }

        public static bool HasExecutionSpeedThrottling(PowerThrottlingState state)
        {
            return (state.StateMask & ExecutionSpeed) != 0;
        }

        public static void EnableDebugPrivilege()
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token))
                {
                    return;
                }

                Luid luid;
                if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out luid))
                {
                    return;
                }

                TokenPrivileges privileges = new TokenPrivileges();
                privileges.PrivilegeCount = 1;
                privileges.Luid = luid;
                privileges.Attributes = SePrivilegeEnabled;
                AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                if (token != IntPtr.Zero)
                {
                    CloseHandle(token);
                }
            }
        }

        private static PowerThrottlingState EmptyState()
        {
            PowerThrottlingState state = new PowerThrottlingState();
            state.Version = CurrentVersion;
            state.ControlMask = 0;
            state.StateMask = 0;
            return state;
        }

        private static PowerThrottlingState HighQosState()
        {
            PowerThrottlingState state = new PowerThrottlingState();
            state.Version = CurrentVersion;
            state.ControlMask = ExecutionSpeed;
            state.StateMask = 0;
            return state;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }
}
