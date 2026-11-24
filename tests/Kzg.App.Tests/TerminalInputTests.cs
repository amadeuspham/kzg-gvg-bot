using System.Runtime.InteropServices;
using Kzg.App.Services;

namespace Kzg.App.Tests;

public partial class TerminalInputTests
{
    [LibraryImport("libc", EntryPoint = "openpty")]
    private static unsafe partial int OpenPty(int* master, int* slave, nint name, nint termios, nint winsize);

    [LibraryImport("libc", EntryPoint = "isatty")]
    private static partial int IsATty(int fd);

    [LibraryImport("libc", EntryPoint = "pipe")]
    private static unsafe partial int Pipe(int* fds);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);

    [Fact]
    public unsafe void A_terminal_as_input_is_replaced_by_an_empty_device_so_a_pause_cannot_stop_the_app_afterwards()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        int master, slave;
        Assert.Equal(0, OpenPty(&master, &slave, 0, 0, 0));
        try
        {
            Assert.Equal(1, IsATty(slave));

            var detached = TerminalInput.Detach(slave);

            Assert.True(detached);
            Assert.Equal(0, IsATty(slave));
        }
        finally
        {
            _ = Close(master);
            _ = Close(slave);
        }
    }

    [Fact]
    public unsafe void Input_that_is_not_a_terminal_is_left_alone()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var fds = stackalloc int[2];
        Assert.Equal(0, Pipe(fds));
        try
        {
            Assert.False(TerminalInput.Detach(fds[0]));
            Assert.Equal(0, IsATty(fds[0]));
        }
        finally
        {
            _ = Close(fds[0]);
            _ = Close(fds[1]);
        }
    }

    [Fact]
    public void A_descriptor_that_is_not_open_is_left_alone()
    {
        Assert.False(TerminalInput.Detach(999));
    }
}
