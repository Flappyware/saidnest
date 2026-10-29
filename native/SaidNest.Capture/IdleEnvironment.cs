using System.Runtime.InteropServices;

namespace SaidNest.Capture;

sealed record IdleSample(double? InputIdleSeconds,double? CpuBusyFraction,bool? OnAcPower);

sealed class IdleEnvironment
{
    [StructLayout(LayoutKind.Sequential)] struct LastInput { public uint Size,Time; }
    [StructLayout(LayoutKind.Sequential)] struct Power { public byte Ac,Battery,Percent,Saver; public uint Remaining,Full; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput input);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out ulong idle,out ulong kernel,out ulong user);
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out Power power);
    ulong? previousTotal,previousIdle;
    public IdleSample Read()
    {
        var input=new LastInput {Size=(uint)Marshal.SizeOf<LastInput>()};
        double? idle=null;
        if(GetLastInputInfo(ref input)) {
            uint delta=unchecked((uint)Environment.TickCount-input.Time);
            // An injected/future timestamp or ambiguous tick wrap must not look idle.
            if(delta<=int.MaxValue)idle=delta/1000.0;
        }
        double? busy=null;
        if(GetSystemTimes(out ulong systemIdle,out ulong kernel,out ulong user)) {
            ulong total=kernel+user;
            if(previousTotal is {} before && previousIdle is {} beforeIdle && total>before && systemIdle>=beforeIdle)
                busy=Math.Clamp(1.0-(double)(systemIdle-beforeIdle)/(total-before),0,1);
            previousTotal=total; previousIdle=systemIdle;
        }
        bool? ac=GetSystemPowerStatus(out var power) && power.Ac!=255?power.Ac==1:null;
        return new(idle,busy,ac);
    }
}
