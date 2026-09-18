using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ContainerToDrive.Windows;

public static class LocalPipe
{
    public static NamedPipeServerStream CreateServer(string root)
    {
        var owner = new SecurityIdentifier(AppPaths.CurrentSid);
        var security = new PipeSecurity();
        // CurrentUserOnly clients compare the pipe owner to TokenOwner, which
        // can be Administrators for an elevated token. The DACL still grants
        // only this user's SID and SYSTEM; peer token/session checks are separate.
        using var identity = WindowsIdentity.GetCurrent();
        security.SetOwner(identity.Owner ?? owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));

        // Important: NamedPipeServerStreamAcl discards a supplied ACL when CurrentUserOnly is set.
        // Use its ACL-at-creation path instead, preserving NETWORK deny with no post-creation race.
        // ValidateClient supplies the additional CurrentUserOnly token/elevation restriction.
        return NamedPipeServerStreamAcl.Create(AppPaths.PipeName(root), PipeDirection.InOut, 16,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 16384, 16384, security,
            HandleInheritability.None);
    }

    /// <summary>Call after every connection and before reading/deserializing any application request.</summary>
    public static void ValidateClient(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ProcessIdentity.ValidateClient(pipe);
    }
}