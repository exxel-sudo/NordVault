namespace NordVault.Core;

public sealed record VolumeInfo
{
    public string MountPoint { get; init; } = "";
    public string VolumeId { get; init; } = "";
    public string Label { get; init; } = "";
    public string FileSystem { get; init; } = "";
    public string DriveType { get; init; } = "";
    public string VolumeType { get; init; } = "";
    public string VolumeStatus { get; init; } = "";
    public string ProtectionStatus { get; init; } = "";
    public string LockStatus { get; init; } = "";
    public string EncryptionMethod { get; init; } = "";
    public int EncryptionPercentage { get; init; }
    public string MediaKind { get; init; } = "Unknown";
    public string BusType { get; init; } = "Unknown";
    public long SizeBytes { get; init; }
    public long FreeBytes { get; init; }
    public int WipingStatus { get; init; } = -1;
    public int WipingPercentage { get; init; }
    public bool IsSystem => VolumeType == "OperatingSystem";
    public bool IsProtected => VolumeStatus == "FullyEncrypted" && ProtectionStatus == "On";
    public string DisplayName => $"{MountPoint}  {Label}";
    public string Capacity => $"{SizeBytes / 1073741824.0:0.0} GB";
    public string StatusText => LockStatus == "Locked" ? "Låst" : VolumeStatus switch
    {
        "FullyEncrypted" => IsProtected ? "Skyddad" : "Krypterad · skydd avstängt",
        "FullyDecrypted" => "Okrypterad",
        "EncryptionInProgress" => $"Krypterar {EncryptionPercentage}%",
        "DecryptionInProgress" => $"Dekrypterar {EncryptionPercentage}%",
        "EncryptionPaused" => "Kryptering pausad",
        "DecryptionPaused" => "Dekryptering pausad",
        _ => "Status okänd"
    };
    public string MediaText => $"{(MediaKind == "Unknown" ? "Okänd" : MediaKind)} · {(BusType == "Unknown" ? "okänd anslutning" : BusType)}";
}

public sealed record DiscoveryResult
{
    public bool IsAdministrator { get; init; }
    public bool BitLockerAvailable { get; init; }
    public bool TpmReady { get; init; }
    public string Message { get; init; } = "";
    public VolumeInfo[] Volumes { get; init; } = [];
}

public sealed record RecoveryMaterial
{
    public string MountPoint { get; init; } = "";
    public string VolumeId { get; init; } = "";
    public string ProtectorId { get; init; } = "";
    public string RecoveryPassword { get; init; } = "";
}

public sealed record BackendRequest
{
    public string Action { get; init; } = "Discover";
    public string MountPoint { get; init; } = "";
    public string VolumeId { get; init; } = "";
    public string ProtectorId { get; init; } = "";
    public string Password { get; init; } = "";
    public string Pin { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string ToolPath { get; init; } = "";
    public bool BackupVerified { get; init; }
    public bool Confirmed { get; init; }
    public bool EulaAccepted { get; init; }
}

public sealed record BackendEnvelope<T>
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public T? Data { get; init; }
}

public sealed record OperationResult
{
    public string Message { get; init; } = "";
}

public sealed record PolicyDecision(bool Allowed, string Reason)
{
    public static PolicyDecision Allow(string reason) => new(true, reason);
    public static PolicyDecision Deny(string reason) => new(false, reason);
}
