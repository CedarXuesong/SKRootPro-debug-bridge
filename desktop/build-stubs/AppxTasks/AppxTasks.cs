using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.Build.AppxPackage;

public sealed class RemovePayloadDuplicates : Microsoft.Build.Utilities.Task
{
    [Required]
    public ITaskItem[] Inputs { get; set; } = Array.Empty<ITaskItem>();

    public string ProjectName { get; set; }
    public string Platform { get; set; }
    public string VsTelemetrySession { get; set; }

    [Output]
    public ITaskItem[] Filtered { get; set; } = Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        Filtered = Inputs;
        return true;
    }
}

public sealed class ExpandPayloadDirectories : Microsoft.Build.Utilities.Task
{
    [Required]
    public ITaskItem[] Inputs { get; set; } = Array.Empty<ITaskItem>();

    public string VsTelemetrySession { get; set; }

    [Output]
    public ITaskItem[] Expanded { get; set; } = Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        Expanded = Inputs;
        return true;
    }
}

public sealed class GetDefaultResourceLanguage : Microsoft.Build.Utilities.Task
{
    public string DefaultLanguage { get; set; } = "en-US";
    public ITaskItem[] SourceAppxManifest { get; set; } = Array.Empty<ITaskItem>();
    public string VsTelemetrySession { get; set; }

    [Output]
    public string DefaultResourceLanguage { get; set; } = "en-US";

    public override bool Execute() => true;
}

public sealed class GetPackageArchitecture : Microsoft.Build.Utilities.Task
{
    public string Platform { get; set; }
    public ITaskItem[] ProjectArchitecture { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] RecursiveProjectArchitecture { get; set; } = Array.Empty<ITaskItem>();
    public string VsTelemetrySession { get; set; }

    [Output]
    public string PackageArchitecture { get; set; } = "x64";

    public override bool Execute() => true;
}

public sealed class ValidateConfiguration : Microsoft.Build.Utilities.Task
{
    public string TargetPlatformMinVersion { get; set; }
    public string TargetPlatformVersion { get; set; }
    public string ProjectLanguage { get; set; }
    public string VsTelemetrySession { get; set; }

    public override bool Execute() => true;
}

public sealed class RemoveRedundantXamlFilesFromSdkPayload : Microsoft.Build.Utilities.Task
{
    public ITaskItem[] Inputs { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] SdkPayload { get; set; } = Array.Empty<ITaskItem>();

    [Output]
    public ITaskItem[] Filtered { get; set; } = Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        Filtered = Inputs;
        return true;
    }
}

public sealed class GetSdkFileFullPath : Microsoft.Build.Utilities.Task
{
    public string FileName { get; set; }
    public string FileArchitecture { get; set; }
    public string TargetPlatformSdkRootOverride { get; set; }
    public bool RequireExeExtension { get; set; }

    [Output]
    public string FullFilePath { get; set; }

    public override bool Execute() => true;
}

public sealed class GetSdkPropertyValue : Microsoft.Build.Utilities.Task
{
    public string TargetPlatformSdkRootOverride { get; set; }
    public string SDKIdentifier { get; set; }
    public string SDKVersion { get; set; }
    public string TargetPlatformIdentifier { get; set; }
    public string TargetPlatformMinVersion { get; set; }
    public string PropertyName { get; set; }

    [Output]
    public string PropertyValue { get; set; }

    public override bool Execute() => true;
}

