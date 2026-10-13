using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

public static class VerificationHandoffNative
{
    [DllImport("kernel32.dll", SetLastError=true)] static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetProcessTimes(SafeProcessHandle h,out long created,out long exited,out long kernel,out long user);
    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(SafeProcessHandle h,uint flags,StringBuilder name,ref int length);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool TerminateProcess(SafeProcessHandle h,uint code);
    [DllImport("kernel32.dll",SetLastError=true)] static extern uint WaitForSingleObject(SafeProcessHandle h,uint ms);
    [DllImport("ntdll.dll")] static extern int NtSuspendProcess(SafeProcessHandle h);
    [DllImport("ntdll.dll")] static extern int NtResumeProcess(SafeProcessHandle h);
    public sealed class FrozenProcess : IDisposable
    {
        readonly SafeProcessHandle handle;bool suspended,terminated;
        internal FrozenProcess(int pid,long expectedCreation,string expectedExecutable)
        {
            handle=OpenProcess(0x100000|0x1000|0x800|1,false,pid);
            if(handle.IsInvalid)throw new Win32Exception();
            try{
                if(!GetProcessTimes(handle,out var created,out _,out _,out _)||created!=expectedCreation)
                    throw new InvalidOperationException("Process creation identity changed.");
                var name=new StringBuilder(32768);int length=name.Capacity;
                if(!QueryFullProcessImageName(handle,0,name,ref length)||!name.ToString().Equals(expectedExecutable,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Process executable identity changed.");
                int status=NtSuspendProcess(handle);
                if(status!=0)throw new InvalidOperationException("Process freeze failed: "+status);
                suspended=true;
            }catch{handle.Dispose();throw;}
        }
        public void StopForVerifiedHandoff()
        {
            if(!suspended||terminated)throw new InvalidOperationException("Process is not frozen for handoff.");
            if(!TerminateProcess(handle,76))throw new Win32Exception();
            terminated=true;
            if(WaitForSingleObject(handle,10000)!=0)throw new InvalidOperationException("Process termination not confirmed; do not launch another operation.");
        }
        public void Dispose()
        {
            if(suspended&&!terminated){int status=NtResumeProcess(handle);if(status!=0)throw new InvalidOperationException("Process thaw failed: "+status);}
            handle.Dispose();
        }
    }
    public static FrozenProcess Freeze(int pid,long expectedCreation,string expectedExecutable)=>new(pid,expectedCreation,expectedExecutable);
}
