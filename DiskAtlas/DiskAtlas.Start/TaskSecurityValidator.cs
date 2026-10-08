namespace DiskAtlas.Startup;

public static class TaskSecurityValidator
{
    public static void Validate(string sddl, string userSid) =>
        TaskSecurityPolicy.Validate(sddl, userSid);

    public static void ValidateTask(string sddl, string userSid) =>
        Validate(sddl, userSid);

    internal static void ValidateFolder(string sddl, string userSid) =>
        TaskSecurityPolicy.ValidateFolder(sddl, userSid);
}
