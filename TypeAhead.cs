using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

// Keys typed while a hotkey run has focus away from the chat input would land in a popup
// or a button, or reach an IME that is not yet renewed. Between Hold and Release they are
// kept back in memory, never logged, and then typed again in the same order.
internal static class TypeAhead
{
    // Marks this program's own injected keys so the hook lets them through.
    internal static readonly UIntPtr OwnKey = new UIntPtr(0x52535754);
    private const int HoldLimitMilliseconds = 3000;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInfo { internal uint Key, Scan, Flags, Time; internal UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message { internal IntPtr Window; internal uint Id; internal IntPtr W, L; internal uint Time; internal int X, Y; }
    private struct HeldKey { internal byte Key, Scan; internal bool Up, Extended; }
    private delegate IntPtr HookProc(int code, IntPtr kind, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr kind, IntPtr data);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string name);

    private static readonly List<HeldKey> held = new List<HeldKey>();
    private static readonly HashSet<uint> pressed = new HashSet<uint>();
    private static HookProc proc;
    private static Thread pump;
    private static uint pumpThread;
    private static volatile bool holding;
    private static int startTick;

    internal static int Count { get { lock (held) return held.Count; } }

    internal static void Hold()
    {
        if (pump != null) return;
        ManualResetEvent ready = new ManualResetEvent(false);
        proc = Hook;
        pump = new Thread(delegate()
        {
            pumpThread = GetCurrentThreadId();
            // A low-level hook runs on the thread that set it, which must pump messages.
            IntPtr hook = SetWindowsHookEx(13, proc, GetModuleHandle(null), 0);
            holding = hook != IntPtr.Zero;
            startTick = Environment.TickCount;
            ready.Set();
            Message message;
            while (hook != IntPtr.Zero && GetMessage(out message, IntPtr.Zero, 0, 0) > 0) { }
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        });
        pump.IsBackground = true;
        pump.Start();
        ready.WaitOne(500);
    }

    private static IntPtr Hook(int code, IntPtr kind, IntPtr data)
    {
        if (code >= 0 && holding)
        {
            if (unchecked(Environment.TickCount - startTick) > HoldLimitMilliseconds) holding = false;
            else
            {
                KeyInfo info = (KeyInfo)Marshal.PtrToStructure(data, typeof(KeyInfo));
                int message = kind.ToInt32();
                bool up = message == 0x101 || message == 0x105;
                // A key that was already down when holding began, such as the hotkey's own Ctrl
                // and Alt, is released at once: only keys pressed during the run are held.
                if (info.Extra != OwnKey && (!up || pressed.Contains(info.Key)))
                {
                    lock (held)
                    {
                        if (up) pressed.Remove(info.Key); else pressed.Add(info.Key);
                        held.Add(new HeldKey { Key = (byte)info.Key, Scan = (byte)info.Scan, Up = up, Extended = (info.Flags & 1) != 0 });
                    }
                    return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, kind, data);
    }

    // Stops holding and types the held keys into whatever window is in front now.
    internal static int Release()
    {
        if (pump == null) return 0;
        holding = false;
        PostThreadMessage(pumpThread, 0x12, IntPtr.Zero, IntPtr.Zero);
        pump.Join(500);
        pump = null;
        HeldKey[] keys;
        lock (held) { keys = held.ToArray(); held.Clear(); pressed.Clear(); }
        foreach (HeldKey key in keys)
        {
            keybd_event(key.Key, key.Scan, (key.Extended ? 1u : 0u) | (key.Up ? 2u : 0u), OwnKey);
            Thread.Sleep(2);
        }
        return keys.Length;
    }
}
