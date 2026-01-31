using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodeBridge.Native;

/// <summary>
/// ConPTY (Pseudo Console) Native API 封装
/// </summary>
public static class ConPtyNative
{
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(
        COORD size,
        SafeFileHandle hInput,
        SafeFileHandle hOutput,
        uint dwFlags,
        out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out SafeFileHandle hReadPipe,
        out SafeFileHandle hWritePipe,
        IntPtr lpPipeAttributes,
        uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        uint dwFlags,
        ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        [In] ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;

        public COORD(short x, short y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    public class PseudoConsole : IDisposable
    {
        public IntPtr Handle { get; private set; }
        public SafeFileHandle InputPipe { get; private set; }
        public SafeFileHandle OutputPipe { get; private set; }
        public StreamWriter Input { get; private set; }
        public StreamReader Output { get; private set; }

        private PseudoConsole(IntPtr handle, SafeFileHandle inputPipe, SafeFileHandle outputPipe)
        {
            Handle = handle;
            InputPipe = inputPipe;
            OutputPipe = outputPipe;
            Input = new StreamWriter(new FileStream(inputPipe, FileAccess.Write)) { AutoFlush = true };
            Output = new StreamReader(new FileStream(outputPipe, FileAccess.Read));
        }

        public static PseudoConsole Create(int cols, int rows)
        {
            if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0))
                throw new InvalidOperationException($"Failed to create input pipe. Error: {Marshal.GetLastWin32Error()}");
            if (!CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
                throw new InvalidOperationException($"Failed to create output pipe. Error: {Marshal.GetLastWin32Error()}");

            var result = CreatePseudoConsole(
                new COORD((short)cols, (short)rows),
                inputRead,
                outputWrite,
                0,
                out var hPC);

            if (result != 0)
                throw new InvalidOperationException($"Failed to create pseudo console. Error: {Marshal.GetLastWin32Error()}");

            inputRead.Dispose();
            outputWrite.Dispose();

            return new PseudoConsole(hPC, inputWrite, outputRead);
        }

        public void Resize(int cols, int rows)
        {
            ResizePseudoConsole(Handle, new COORD((short)cols, (short)rows));
        }

        public void Dispose()
        {
            Input?.Dispose();
            Output?.Dispose();
            if (Handle != IntPtr.Zero)
            {
                ClosePseudoConsole(Handle);
                Handle = IntPtr.Zero;
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    public static System.Diagnostics.Process CreateProcess(string command, string? workingDirectory, IntPtr hPC, out SafeProcessHandle processHandle)
    {
        var startupInfo = new STARTUPINFOEX();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

        // 第一次调用：获取所需的缓冲区大小
        // 注意：此调用预期返回 false，错误码 122 (ERROR_INSUFFICIENT_BUFFER) 是正常的
        var lpSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);

        // 检查是否获取到有效的大小
        if (lpSize == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to get attribute list size. Error: {Marshal.GetLastWin32Error()}");

        // 第二次调用：使用正确大小的缓冲区初始化
        startupInfo.lpAttributeList = Marshal.AllocHGlobal(lpSize);
        if (!InitializeProcThreadAttributeList(startupInfo.lpAttributeList, 1, 0, ref lpSize))
            throw new InvalidOperationException($"Failed to initialize attribute list. Error: {Marshal.GetLastWin32Error()}");

        try
        {
            // 注意：lpValue 应该直接传递 hPC，而不是指向 hPC 的指针
            if (!UpdateProcThreadAttribute(
                    startupInfo.lpAttributeList,
                    0,
                    (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    hPC,  // 直接传递 PseudoConsole 句柄
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new InvalidOperationException($"Failed to update attribute list. Error: {Marshal.GetLastWin32Error()}");
            }

            var success = CreateProcess(
                null,
                command,
                IntPtr.Zero,
                IntPtr.Zero,
                false, // ConPTY 不需要继承句柄，PseudoConsole 通过属性列表传递
                EXTENDED_STARTUPINFO_PRESENT,
                IntPtr.Zero,
                string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
                ref startupInfo,
                out var processInfo);

            if (!success)
                throw new InvalidOperationException($"Failed to create process. Error: {Marshal.GetLastWin32Error()}");

            processHandle = new SafeProcessHandle(processInfo.hProcess, ownsHandle: true);
            CloseHandle(processInfo.hThread);

            // 获取托管 Process 对象（注意：不要在这里关闭 processHandle）
            var process = System.Diagnostics.Process.GetProcessById(processInfo.dwProcessId);
            return process;
        }
        finally
        {
            // 清理资源（防止内存泄漏）
            DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
            Marshal.FreeHGlobal(startupInfo.lpAttributeList);
        }
    }
}
