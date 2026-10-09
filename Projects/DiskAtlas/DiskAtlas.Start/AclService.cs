using System.Security.AccessControl;
using System.Security.Principal;

namespace DiskAtlas.Startup;

internal static class AclService
{
    private static readonly SecurityIdentifier Administrators = new("S-1-5-32-544");
    private static readonly SecurityIdentifier Users = new("S-1-5-32-545");
    private static readonly SecurityIdentifier System = new("S-1-5-18");

    internal static void ProtectInstallation(string path, bool recursive = true)
    {
        ApplyDirectory(path, Administrators, System, Administrators, System, Users, setOwnerAndGroup: true);
        if (!recursive)
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(path))
        {
            RejectReparse(file);
            ApplyFile(file, Administrators, System, Administrators, System, Users, setOwnerAndGroup: true);
        }

        foreach (string directory in Directory.EnumerateDirectories(path))
        {
            RejectReparse(directory);
            ProtectInstallation(directory, recursive: true);
        }
    }

    internal static void ProtectStage(string path, string sid)
    {
        var user = new SecurityIdentifier(sid);
        ApplyStageTree(path, user, FileSystemRights.ReadAndExecute);
    }

    internal static void RestoreStageAccess(string path, string sid)
    {
        if (Directory.Exists(path))
        {
            ApplyStageTree(path, new SecurityIdentifier(sid), FileSystemRights.FullControl);
        }
    }

    internal static void ValidateProtectedInstallation(string path, bool directory)
    {
        FileSystemSecurity security = directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!Administrators.Equals(security.GetOwner(typeof(SecurityIdentifier))) || !security.AreAccessRulesProtected)
        {
            throw new UnauthorizedAccessException("설치 경로의 소유권 또는 ACL 상속 설정이 올바르지 않습니다.");
        }

        bool administratorsFull = false;
        bool systemFull = false;
        bool usersReadExecute = false;
        int ruleCount = 0;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            ruleCount++;
            if (rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference is not SecurityIdentifier identity)
            {
                throw new UnauthorizedAccessException("설치 경로에 예상하지 않은 접근 규칙이 있습니다.");
            }

            if (Administrators.Equals(identity))
            {
                administratorsFull |= (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
            }
            else if (System.Equals(identity))
            {
                systemFull |= (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
            }
            else if (Users.Equals(identity))
            {
                const FileSystemRights writeRights = FileSystemRights.WriteData |
                    FileSystemRights.AppendData |
                    FileSystemRights.WriteAttributes |
                    FileSystemRights.WriteExtendedAttributes |
                    FileSystemRights.Delete |
                    FileSystemRights.DeleteSubdirectoriesAndFiles |
                    FileSystemRights.ChangePermissions |
                    FileSystemRights.TakeOwnership;
                usersReadExecute |= (rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute &&
                    (rule.FileSystemRights & writeRights) == 0;
            }
            else
            {
                throw new UnauthorizedAccessException("설치 경로에 예상하지 않은 사용자의 접근 규칙이 있습니다.");
            }
        }

        if (ruleCount != 3 || !administratorsFull || !systemFull || !usersReadExecute)
        {
            throw new UnauthorizedAccessException("설치 경로의 접근 권한이 올바르지 않습니다.");
        }
    }

    private static void ApplyStageTree(string path, SecurityIdentifier user, FileSystemRights userRights)
    {
        ApplyDirectory(path, user, user, Administrators, System, user, userRights, setOwnerAndGroup: false);
        foreach (string file in Directory.EnumerateFiles(path))
        {
            RejectReparse(file);
            ApplyFile(file, user, user, Administrators, System, user, userRights, setOwnerAndGroup: false);
        }

        foreach (string directory in Directory.EnumerateDirectories(path))
        {
            RejectReparse(directory);
            ApplyStageTree(directory, user, userRights);
        }
    }

    private static void ApplyDirectory(
        string path,
        SecurityIdentifier owner,
        SecurityIdentifier group,
        SecurityIdentifier first,
        SecurityIdentifier second,
        SecurityIdentifier reader,
        FileSystemRights readerRights = FileSystemRights.ReadAndExecute,
        bool setOwnerAndGroup = true)
    {
        RejectReparse(path);
        var security = new DirectorySecurity();
        if (setOwnerAndGroup)
        {
            security.SetOwner(owner);
            security.SetGroup(group);
        }
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(first, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(second, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(reader, readerRights, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void ApplyFile(
        string path,
        SecurityIdentifier owner,
        SecurityIdentifier group,
        SecurityIdentifier first,
        SecurityIdentifier second,
        SecurityIdentifier reader,
        FileSystemRights readerRights = FileSystemRights.ReadAndExecute,
        bool setOwnerAndGroup = true)
    {
        RejectReparse(path);
        var security = new FileSecurity();
        if (setOwnerAndGroup)
        {
            security.SetOwner(owner);
            security.SetGroup(group);
        }
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(first, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(second, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(reader, readerRights, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"재분석 지점의 ACL은 변경하지 않습니다: {path}");
        }
    }
}
