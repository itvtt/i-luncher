using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DiskAtlas.Startup;

public sealed record TaskDefinitionSnapshot(
    string UserId,
    int LogonType,
    int RunLevel,
    string ExecutablePath,
    string Arguments,
    string WorkingDirectory,
    bool Enabled,
    bool AllowDemandStart,
    bool DisallowStartIfOnBatteries,
    bool StopIfGoingOnBatteries,
    int MultipleInstances,
    string ExecutionTimeLimit,
    int TriggerCount,
    int ActionCount);

public static class TaskDefinitionValidator
{
    public static void Validate(TaskDefinitionSnapshot snapshot, string expectedSid, string expectedAppPath)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(snapshot.UserId, expectedSid) ||
            snapshot.LogonType != 3 ||
            snapshot.RunLevel != 1 ||
            !Path.GetFullPath(snapshot.ExecutablePath).Equals(Path.GetFullPath(expectedAppPath), StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(snapshot.Arguments) ||
            !Path.GetFullPath(snapshot.WorkingDirectory).Equals(Path.GetDirectoryName(Path.GetFullPath(expectedAppPath)), StringComparison.OrdinalIgnoreCase) ||
            !snapshot.Enabled ||
            !snapshot.AllowDemandStart ||
            snapshot.DisallowStartIfOnBatteries ||
            snapshot.StopIfGoingOnBatteries ||
            snapshot.MultipleInstances != 2 ||
            !snapshot.ExecutionTimeLimit.Equals("PT0S", StringComparison.OrdinalIgnoreCase) ||
            snapshot.TriggerCount != 0 ||
            snapshot.ActionCount != 1)
        {
            throw new InvalidOperationException("등록된 DiskAtlas 작업의 보안 설정이 예상과 다릅니다. 다시 설치해 주세요.");
        }
    }
}

internal static class TaskSecurityPolicy
{
    private const int FileAllAccess = 0x001F01FF;
    private const int GenericReadExecute = unchecked((int)0xA0000000);
    private const int FileReadExecute = 0x001200A9;
    private static readonly SecurityIdentifier Administrators = new("S-1-5-32-544");
    private static readonly SecurityIdentifier System = new("S-1-5-18");
    private static readonly SecurityIdentifier Users = new("S-1-5-32-545");

    internal static void Validate(string sddl, string userSid) =>
        Validate(sddl, new Dictionary<SecurityIdentifier, int>
        {
            [Administrators] = FileAllAccess,
            [System] = FileAllAccess,
            [new SecurityIdentifier(userSid)] = GenericReadExecute,
        });

    internal static void ValidateFolder(string sddl, string userSid) =>
        Validate(sddl, new Dictionary<SecurityIdentifier, int>
        {
            [Administrators] = FileAllAccess,
            [System] = FileAllAccess,
            [Users] = GenericReadExecute,
            [new SecurityIdentifier(userSid)] = GenericReadExecute,
        });

    private static void Validate(string sddl, IReadOnlyDictionary<SecurityIdentifier, int> expected)
    {
        var descriptor = new RawSecurityDescriptor(sddl);
        if (descriptor.Owner is not SecurityIdentifier owner ||
            !Administrators.Equals(owner) ||
            descriptor.Group is not SecurityIdentifier group ||
            !System.Equals(group) ||
            (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 ||
            descriptor.DiscretionaryAcl is null ||
            descriptor.DiscretionaryAcl.Count != expected.Count)
        {
            throw new UnauthorizedAccessException("예약 작업의 소유권 또는 DACL이 올바르지 않습니다.");
        }

        var remaining = new Dictionary<SecurityIdentifier, int>(expected);
        foreach (GenericAce genericAce in descriptor.DiscretionaryAcl)
        {
            if (genericAce is not CommonAce ace ||
                ace.AceQualifier != AceQualifier.AccessAllowed ||
                ace.AceFlags != AceFlags.None ||
                !remaining.Remove(ace.SecurityIdentifier, out int expectedMask) ||
                !HasExpectedAccess(ace.AccessMask, expectedMask))
            {
                throw new UnauthorizedAccessException("예약 작업에 예상하지 않은 접근 규칙이 있습니다.");
            }
        }

        if (remaining.Count != 0)
        {
            throw new UnauthorizedAccessException("예약 작업의 필수 접근 규칙이 없습니다.");
        }
    }

    private static bool HasExpectedAccess(int actualMask, int expectedMask) =>
        actualMask == expectedMask ||
        (expectedMask == GenericReadExecute && actualMask == FileReadExecute);
}

internal sealed class TaskSchedulerService
{
    private const int TaskCreateOrUpdateWithoutPrincipalAce = 0x16;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const int TaskActionExecute = 0;
    private const int TaskInstancesIgnoreNew = 2;

    private readonly InstallationPaths _paths;

    private sealed record ExistingTaskSnapshot(
        string Xml,
        string SecurityDescriptor,
        string UserId,
        int LogonType);

    internal TaskSchedulerService(InstallationPaths paths) => _paths = paths;

    internal void Register(string appPath)
    {
        PathPolicy.RejectExistingReparsePoints(appPath);
        object? serviceObject = null;
        object? rootObject = null;
        object? folderObject = null;
        object? definitionObject = null;
        object? actionObject = null;
        object? registeredObject = null;
        ExistingTaskSnapshot? previousTask = null;
        bool registrationAttempted = false;
        try
        {
            serviceObject = CreateService();
            dynamic service = serviceObject;
            service.Connect();
            rootObject = service.GetFolder("\\");
            dynamic root = rootObject;

            string folderSddl = BuildFolderSddl(_paths.UserSid);
            try
            {
                folderObject = root.GetFolder(InstallationPaths.TaskFolderPath);
            }
            catch (COMException)
            {
                folderObject = root.CreateFolder("i-luncher", folderSddl);
            }

            dynamic folder = folderObject;
            folder.SetSecurityDescriptor(folderSddl, 0);
            TaskSecurityValidator.ValidateFolder(Convert.ToString(folder.GetSecurityDescriptor(7)) ?? string.Empty, _paths.UserSid);

            definitionObject = service.NewTask(0);
            dynamic definition = definitionObject;
            definition.RegistrationInfo.Description = "DiskAtlas를 현재 사용자 세션에서 관리자 권한으로 시작합니다.";
            definition.Principal.UserId = _paths.UserSid;
            definition.Principal.LogonType = TaskLogonInteractiveToken;
            definition.Principal.RunLevel = TaskRunLevelHighest;
            definition.Settings.Enabled = true;
            definition.Settings.AllowDemandStart = true;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.MultipleInstances = TaskInstancesIgnoreNew;
            definition.Settings.ExecutionTimeLimit = "PT0S";

            actionObject = definition.Actions.Create(TaskActionExecute);
            dynamic action = actionObject;
            action.Path = Path.GetFullPath(appPath);
            action.Arguments = string.Empty;
            action.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(appPath))!;

            string taskSddl = BuildTaskSddl(_paths.UserSid);
            previousTask = CaptureExistingTask(folder);
            registrationAttempted = true;
            registeredObject = folder.RegisterTaskDefinition(
                _paths.TaskName,
                definition,
                TaskCreateOrUpdateWithoutPrincipalAce,
                _paths.UserSid,
                null,
                TaskLogonInteractiveToken,
                taskSddl);

            ValidateRegisteredTask(folder, appPath);
        }
        catch (Exception registrationException) when (registrationAttempted && folderObject is not null)
        {
            try
            {
                RestorePreviousTask((dynamic)folderObject, previousTask);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "The scheduled task update failed, and its previous definition could not be restored.",
                    registrationException,
                    rollbackException);
            }

            throw;
        }
        finally
        {
            Release(registeredObject);
            Release(actionObject);
            Release(definitionObject);
            Release(folderObject);
            Release(rootObject);
            Release(serviceObject);
        }
    }

    internal void ValidateAndRun(string appPath)
    {
        object? serviceObject = null;
        object? folderObject = null;
        object? taskObject = null;
        object? runningObject = null;
        try
        {
            serviceObject = CreateService();
            dynamic service = serviceObject;
            service.Connect();
            folderObject = service.GetFolder(InstallationPaths.TaskFolderPath);
            dynamic folder = folderObject;
            taskObject = folder.GetTask(_paths.TaskName);
            ValidateTaskObject(taskObject, appPath);
            dynamic task = taskObject;
            runningObject = task.Run(null);
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException("DiskAtlas 예약 작업을 찾거나 시작하지 못했습니다. 다시 설치해 주세요.", exception);
        }
        finally
        {
            Release(runningObject);
            Release(taskObject);
            Release(folderObject);
            Release(serviceObject);
        }
    }

    internal void Delete()
    {
        object? serviceObject = null;
        object? folderObject = null;
        try
        {
            serviceObject = CreateService();
            dynamic service = serviceObject;
            service.Connect();
            try
            {
                folderObject = service.GetFolder(InstallationPaths.TaskFolderPath);
                dynamic folder = folderObject;
                try
                {
                    folder.DeleteTask(_paths.TaskName, 0);
                }
                catch (COMException exception) when ((uint)exception.HResult == 0x80070002u)
                {
                }
            }
            catch (COMException exception) when ((uint)exception.HResult == 0x80070002u)
            {
            }
        }
        finally
        {
            Release(folderObject);
            Release(serviceObject);
        }
    }

    private void ValidateRegisteredTask(dynamic folder, string appPath)
    {
        object? taskObject = null;
        try
        {
            taskObject = folder.GetTask(_paths.TaskName);
            ValidateTaskObject(taskObject, appPath);
        }
        finally
        {
            Release(taskObject);
        }
    }

    private ExistingTaskSnapshot? CaptureExistingTask(dynamic folder)
    {
        object? taskObject = null;
        try
        {
            taskObject = folder.GetTask(_paths.TaskName);
            dynamic task = taskObject;
            dynamic principal = task.Definition.Principal;
            return new ExistingTaskSnapshot(
                Convert.ToString(task.Xml) ?? string.Empty,
                Convert.ToString(task.GetSecurityDescriptor(7)) ?? string.Empty,
                Convert.ToString(principal.UserId) ?? string.Empty,
                Convert.ToInt32(principal.LogonType));
        }
        catch (COMException exception) when ((uint)exception.HResult == 0x80070002u)
        {
            return null;
        }
        finally
        {
            Release(taskObject);
        }
    }

    private void RestorePreviousTask(dynamic folder, ExistingTaskSnapshot? previousTask)
    {
        if (previousTask is null)
        {
            try
            {
                folder.DeleteTask(_paths.TaskName, 0);
            }
            catch (COMException exception) when ((uint)exception.HResult == 0x80070002u)
            {
            }

            return;
        }

        object? restoredObject = null;
        try
        {
            restoredObject = folder.RegisterTask(
                _paths.TaskName,
                previousTask.Xml,
                TaskCreateOrUpdateWithoutPrincipalAce,
                previousTask.UserId,
                null,
                previousTask.LogonType,
                previousTask.SecurityDescriptor);
        }
        finally
        {
            Release(restoredObject);
        }
    }

    private void ValidateTaskObject(dynamic task, string appPath)
    {
        dynamic definition = task.Definition;
        dynamic actions = definition.Actions;
        object? actionObject = actions.Count == 1 ? actions.Item(1) : null;
        dynamic? action = actionObject;
        if (action is not null && Convert.ToInt32(action.Type) != TaskActionExecute)
        {
            throw new InvalidOperationException("등록된 DiskAtlas 작업의 실행 동작 형식이 올바르지 않습니다.");
        }
        var snapshot = new TaskDefinitionSnapshot(
            Convert.ToString(definition.Principal.UserId) ?? string.Empty,
            Convert.ToInt32(definition.Principal.LogonType),
            Convert.ToInt32(definition.Principal.RunLevel),
            Convert.ToString(action?.Path) ?? string.Empty,
            Convert.ToString(action?.Arguments) ?? string.Empty,
            Convert.ToString(action?.WorkingDirectory) ?? string.Empty,
            Convert.ToBoolean(definition.Settings.Enabled),
            Convert.ToBoolean(definition.Settings.AllowDemandStart),
            Convert.ToBoolean(definition.Settings.DisallowStartIfOnBatteries),
            Convert.ToBoolean(definition.Settings.StopIfGoingOnBatteries),
            Convert.ToInt32(definition.Settings.MultipleInstances),
            Convert.ToString(definition.Settings.ExecutionTimeLimit) ?? string.Empty,
            Convert.ToInt32(definition.Triggers.Count),
            Convert.ToInt32(actions.Count));
        TaskDefinitionValidator.Validate(snapshot, _paths.UserSid, appPath);
        TaskSecurityValidator.Validate(Convert.ToString(task.GetSecurityDescriptor(7)) ?? string.Empty, _paths.UserSid);
    }

    private static object CreateService()
    {
        Type serviceType = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        return Activator.CreateInstance(serviceType)
            ?? throw new InvalidOperationException("Windows 작업 스케줄러에 연결하지 못했습니다.");
    }

    private static string BuildTaskSddl(string sid) =>
        $"O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;{sid})";

    private static string BuildFolderSddl(string sid) =>
        $"O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;BU)(A;;GRGX;;;{sid})";

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
