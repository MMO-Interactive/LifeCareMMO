using System.Security.AccessControl;
using System.Security.Principal;

namespace StormChase.Server;

internal static class ServerDataAccess
{
    public static void SecureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if(OperatingSystem.IsWindows())
        {
            var security=new DirectorySecurity();security.SetAccessRuleProtection(true,false);
            var inheritance=InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit;
            foreach(var identity in new[]{WindowsIdentity.GetCurrent().User!,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})
                security.AddAccessRule(new(identity,FileSystemRights.FullControl,inheritance,PropagationFlags.None,AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
    }
}
