using System.ComponentModel;
using System.Runtime.InteropServices;

// Only allocations returned by VirtualAlloc in THIS process can be written,
// made executable or released. No OpenProcess or remote memory API exists.
sealed class OwnedNativeMemory : IDisposable
{
    private readonly int _length;
    public nint Address { get; private set; }
    public OwnedNativeMemory(int length)
    {
        _length = length;
        Address = VirtualAlloc(0, (nuint)length, 0x3000, 0x04);
        if (Address == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Write(int offset, byte[] bytes)
    {
        Check(offset, bytes.Length); Marshal.Copy(bytes, 0, Address + offset, bytes.Length);
    }
    public byte[] Read(int offset, int count)
    {
        Check(offset, count); byte[] bytes = new byte[count]; Marshal.Copy(Address + offset, bytes, 0, count); return bytes;
    }
    public void ExecutablePage(int offset = 0)
    {
        if (offset % 4096 != 0) throw new ArgumentOutOfRangeException(nameof(offset));
        Check(offset, 4096);
        if (!VirtualProtect(Address + offset, 4096, 0x20, out _) || !FlushInstructionCache(GetCurrentProcess(), Address + offset, 4096))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private void Check(int offset, int count)
    {
        if (Address == 0 || offset < 0 || count < 0 || (long)offset + count > _length)
            throw new InvalidOperationException("Outside owned fixture allocation.");
    }
    public void Dispose()
    {
        if (Address == 0) return;
        if (!VirtualFree(Address, 0, 0x8000)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Address = 0;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint old);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFree(nint address, nuint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
}
