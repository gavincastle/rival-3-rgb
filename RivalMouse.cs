using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

internal static class RivalMouse
{
    private const string LightingInterface = @"\\?\hid#vid_1038&pid_1870&mi_03#";
    private const int ReportLength = 65;
    internal const byte AllZones = 0x07;
    internal static readonly Guid HidInterfaceClass = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");

    internal static bool IsLightingInterface(string path)
    {
        return path.StartsWith(LightingInterface, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task SetColors(Color[] colors)
    {
        using (var device = Open())
        using (var stream = new FileStream(device, FileAccess.ReadWrite, 1, true))
        {
            // On firmware 1.1.5 the built-in effect can keep running after a color write.
            // Disable rainbow and reactive lighting, then redraw the full manual palette.
            await Send(stream, device, StartupModeReport(false, false)).ConfigureAwait(false);
            await Send(stream, device, ColorReport(AllZones, colors)).ConfigureAwait(false);
        }
    }

    internal static async Task SaveStartupMode(bool rainbow, bool reactive)
    {
        using (var device = Open())
        using (var stream = new FileStream(device, FileAccess.ReadWrite, 1, true))
        {
            await Send(stream, device, StartupModeReport(rainbow, reactive)).ConfigureAwait(false);
            var save = new byte[ReportLength];
            save[1] = 0x11;
            await Send(stream, device, save).ConfigureAwait(false);
        }
    }

    internal static byte[] StartupModeReport(bool rainbow, bool reactive)
    {
        var report = new byte[ReportLength];
        report[1] = 0x27;
        report[2] = rainbow ? (byte)1 : (byte)0;
        report[3] = reactive ? (byte)1 : (byte)0;
        return report;
    }

    internal static byte[] ColorReport(byte zones, Color[] colors)
    {
        // Byte zero is the unnumbered HID report ID. RGB slots are positional.
        byte[] report = new byte[ReportLength];
        report[1] = 0x21;
        report[2] = zones;
        for (int zone = 0; zone < 3; zone++)
        {
            int offset = 3 + zone * 3;
            report[offset] = colors[zone].R;
            report[offset + 1] = colors[zone].G;
            report[offset + 2] = colors[zone].B;
        }

        return report;
    }

    internal static SafeFileHandle Open()
    {
        Guid hidClass = HidInterfaceClass;
        const int presentInterfaces = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int length;
            int result = CM_Get_Device_Interface_List_SizeW(out length, ref hidClass, IntPtr.Zero, presentInterfaces);
            if (result != 0)
                throw new InvalidOperationException("Windows could not list USB devices (0x" + result.ToString("X") + ").");

            IntPtr buffer = Marshal.AllocHGlobal(length * sizeof(char));
            try
            {
                result = CM_Get_Device_Interface_ListW(ref hidClass, IntPtr.Zero, buffer, length, presentInterfaces);
                if (result == 0x1A) // The interface list grew between the two calls.
                    continue;
                if (result != 0)
                    throw new InvalidOperationException("Windows could not list USB devices (0x" + result.ToString("X") + ").");

                string[] paths = Marshal.PtrToStringUni(buffer, length).Split('\0');
                string match = null;
                foreach (string path in paths)
                {
                    if (!IsLightingInterface(path))
                        continue;
                    if (match != null)
                        throw new InvalidOperationException("More than one Rival 3 Gen 2 is connected. Connect one at a time.");
                    match = path;
                }

                if (match == null)
                    throw new InvalidOperationException("Connect a wired Rival 3 Gen 2 and try again.");

                const uint genericReadWrite = 0xC0000000;
                const uint shareReadWrite = 3;
                const uint openExisting = 3;
                const uint overlapped = 0x40000000;
                SafeFileHandle device = CreateFileW(match, genericReadWrite, shareReadWrite, IntPtr.Zero, openExisting, overlapped, IntPtr.Zero);
                if (!device.IsInvalid)
                    return device;

                int error = Marshal.GetLastWin32Error();
                device.Dispose();
                throw new Win32Exception(error, "Could not open the mouse. Close SteelSeries GG or other RGB software and try again.");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidOperationException("The USB device list changed. Try again.");
    }

    internal static async Task<byte[]> Send(FileStream stream, SafeFileHandle device, byte[] report)
    {
        const int timeoutMs = 750;
        var timer = Stopwatch.StartNew();
        await AwaitIo(stream.WriteAsync(report, 0, report.Length), device, timeoutMs).ConfigureAwait(false);

        var reply = new byte[ReportLength];
        while (timer.ElapsedMilliseconds < timeoutMs)
        {
            Task<int> read = stream.ReadAsync(reply, 0, reply.Length);
            await AwaitIo(read, device, Math.Max(1, timeoutMs - (int)timer.ElapsedMilliseconds)).ConfigureAwait(false);
            if (read.Result >= 2 && reply[0] == 0 && reply[1] == report[1])
                return reply;
            if (read.Result == 0)
                throw new IOException("The mouse disconnected. Reconnect it and try again.");
        }

        throw new TimeoutException("The mouse did not confirm the lighting change.");
    }

    private static async Task AwaitIo(Task operation, SafeFileHandle device, int timeoutMs)
    {
        if (await Task.WhenAny(operation, Task.Delay(timeoutMs)).ConfigureAwait(false) != operation)
        {
            CancelIoEx(device, IntPtr.Zero);
            try { await operation.ConfigureAwait(false); }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            throw new TimeoutException("The mouse did not confirm the lighting change.");
        }

        await operation.ConfigureAwait(false);
    }

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out int length, ref Guid interfaceClass, IntPtr deviceId, int flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClass, IntPtr deviceId, IntPtr buffer, int length, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle device, IntPtr overlapped);
}
