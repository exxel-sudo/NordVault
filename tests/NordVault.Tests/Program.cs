using NordVault.Core;

var environment = new DiscoveryResult { IsAdministrator = true, BitLockerAvailable = true, TpmReady = true };
var data = new VolumeInfo
{
    MountPoint = "Z:", VolumeId = @"\\?\Volume{9ac1a522-15e4-4926-bb4d-94fc26467942}\",
    DriveType = "Fixed", VolumeType = "FixedData", FileSystem = "NTFS", MediaKind = "HDD",
    LockStatus = "Unlocked", VolumeStatus = "FullyDecrypted", ProtectionStatus = "Off", WipingStatus = 0
};
var protectedData = data with { VolumeStatus = "FullyEncrypted", ProtectionStatus = "On" };
var tests = new List<(string Name, Action Run)>();
void Add(string name, Action test) => tests.Add((name, test));
void Expect(bool value, string message = "Förväntat villkor uppfylldes inte.")
{
    if (!value) throw new InvalidOperationException(message);
}
void DenyAll(VolumeInfo volume)
{
    Expect(!SafetyPolicy.Encrypt(volume, environment).Allowed);
    Expect(!SafetyPolicy.WipeFreeSpace(volume, environment).Allowed);
    Expect(!SafetyPolicy.ShredFile(volume, environment).Allowed);
}
void Reject(Action action)
{
    try { action(); }
    catch (Exception exception) when (exception is ArgumentException or IOException or System.ComponentModel.Win32Exception) { return; }
    throw new InvalidOperationException("Osäker indata accepterades.");
}

Add("HDD data encryption allowed", () => Expect(SafetyPolicy.Encrypt(data, environment).Allowed));
Add("Administrative access required", () =>
{
    var unprivileged = environment with { IsAdministrator = false };
    Expect(!SafetyPolicy.Encrypt(data, unprivileged).Allowed);
    Expect(!SafetyPolicy.WipeFreeSpace(protectedData, unprivileged).Allowed);
    Expect(!SafetyPolicy.ShredFile(data, unprivileged).Allowed);
});
Add("BitLocker unavailable denies operations", () =>
{
    var unavailable = environment with { BitLockerAvailable = false };
    Expect(!SafetyPolicy.Encrypt(data, unavailable).Allowed);
    Expect(!SafetyPolicy.WipeFreeSpace(protectedData, unavailable).Allowed);
    Expect(!SafetyPolicy.ShredFile(data, unavailable).Allowed);
});
Add("Command-injection and malformed mount points rejected", () =>
{
    foreach (var mount in new[] { "", "z:", "ZZ:", "Z:\\", "Z:\n", "Z:;Remove-Item C:\\", "Z:'", "\\\\server\\share", "Z:\0" })
        DenyAll(data with { MountPoint = mount });
});
Add("Missing volume identity rejected", () => DenyAll(data with { VolumeId = " " }));
Add("Network and unknown drive types rejected", () =>
{
    foreach (var type in new[] { "Network", "Unknown", "CDRom", "Ram" }) DenyAll(data with { DriveType = type });
});
Add("Active or paused encryption cannot restart", () =>
{
    foreach (var status in new[] { "FullyEncrypted", "EncryptionInProgress", "DecryptionInProgress", "EncryptionPaused", "DecryptionPaused", "Unknown" })
        Expect(!SafetyPolicy.Encrypt(data with { VolumeStatus = status }, environment).Allowed);
});
Add("Encryption rejects inconsistent protection state", () => Expect(!SafetyPolicy.Encrypt(data with { ProtectionStatus = "On" }, environment).Allowed));
Add("Unknown or active wipe state rejects encryption and shredding", () =>
{
    foreach (var state in new[] { -1, 2, 3, 4 })
    {
        Expect(!SafetyPolicy.Encrypt(data with { WipingStatus = state }, environment).Allowed);
        Expect(!SafetyPolicy.ShredFile(data with { WipingStatus = state }, environment).Allowed);
    }
    Expect(!SafetyPolicy.Encrypt(new VolumeInfo(), environment).Allowed);
    Expect(!SafetyPolicy.ShredFile(data with { VolumeStatus = "Unknown" }, environment).Allowed);
});
Add("Locked volumes rejected", () =>
{
    Expect(!SafetyPolicy.Encrypt(data with { LockStatus = "Locked" }, environment).Allowed);
    Expect(!SafetyPolicy.WipeFreeSpace(protectedData with { LockStatus = "Locked" }, environment).Allowed);
    Expect(!SafetyPolicy.ShredFile(data with { LockStatus = "Locked" }, environment).Allowed);
});
Add("USB encryption accepts supported FAT and exFAT filesystems", () =>
{
    foreach (var fileSystem in new[] { "NTFS", "FAT", "FAT16", "fat32", "exfat" })
        Expect(SafetyPolicy.Encrypt(data with { DriveType = "Removable", VolumeType = "RemovableData", FileSystem = fileSystem, BusType = "USB", MediaKind = "SSD" }, environment).Allowed);
    foreach (var fileSystem in new[] { "", "ReFS", "ext4", "Unknown" })
        Expect(!SafetyPolicy.Encrypt(data with { FileSystem = fileSystem }, environment).Allowed);
});
Add("System encryption requires fixed NTFS and ready TPM", () =>
{
    var system = data with { VolumeType = "OperatingSystem" };
    Expect(SafetyPolicy.Encrypt(system, environment).Allowed);
    Expect(!SafetyPolicy.Encrypt(system, environment with { TpmReady = false }).Allowed);
    Expect(!SafetyPolicy.Encrypt(system with { FileSystem = "exFAT" }, environment).Allowed);
    Expect(!SafetyPolicy.Encrypt(system with { DriveType = "Removable" }, environment).Allowed);
});
Add("Free-space wipe requires complete encryption and enabled protection", () =>
{
    Expect(SafetyPolicy.WipeFreeSpace(protectedData, environment).Allowed);
    Expect(!SafetyPolicy.WipeFreeSpace(data, environment).Allowed);
    Expect(!SafetyPolicy.WipeFreeSpace(protectedData with { ProtectionStatus = "Off" }, environment).Allowed);
    Expect(!SafetyPolicy.WipeFreeSpace(protectedData with { VolumeStatus = "EncryptionInProgress" }, environment).Allowed);
});
Add("Concurrent or paused free-space wipe rejected", () =>
{
    foreach (var state in new[] { -1, 2, 3, 4 })
        Expect(!SafetyPolicy.WipeFreeSpace(protectedData with { WipingStatus = state }, environment).Allowed);
});
Add("SSD and unknown storage explain physical-erasure limitation", () =>
{
    foreach (var media in new[] { "SSD", "Unknown", "Flash" })
    {
        var volume = protectedData with { MediaKind = media };
        var wipe = SafetyPolicy.WipeFreeSpace(volume, environment);
        Expect(wipe.Allowed && wipe.Reason.Contains("fysisk radering garanteras inte", StringComparison.Ordinal));
        Expect(!SafetyPolicy.ShredFile(volume, environment).Allowed);
    }
});
Add("File shred allowed only for known HDD with NTFS", () =>
{
    Expect(SafetyPolicy.ShredFile(data, environment).Allowed);
    Expect(SafetyPolicy.ShredFile(data with { DriveType = "Removable", BusType = "USB" }, environment).Allowed);
    Expect(!SafetyPolicy.ShredFile(data with { FileSystem = "exFAT" }, environment).Allowed);
});
Add("Password requirements and exact confirmation", () =>
{
    var passphrase = "En lång unik lösenfras!";
    Expect(SafetyPolicy.ValidatePassword(passphrase, passphrase).Allowed);
    Expect(SafetyPolicy.ValidatePassword(new string('a', 14), new string('a', 14)).Allowed);
    Expect(SafetyPolicy.ValidatePassword(new string('a', 256), new string('a', 256)).Allowed);
    Expect(!SafetyPolicy.ValidatePassword(new string('a', 13), new string('a', 13)).Allowed);
    Expect(!SafetyPolicy.ValidatePassword(new string('a', 257), new string('a', 257)).Allowed);
    Expect(!SafetyPolicy.ValidatePassword(new string(' ', 14), new string(' ', 14)).Allowed);
    Expect(!SafetyPolicy.ValidatePassword(passphrase, passphrase.ToUpperInvariant()).Allowed);
    Expect(!SafetyPolicy.ValidatePassword(passphrase + "\n", passphrase + "\n").Allowed);
    Expect(!SafetyPolicy.ValidatePassword(passphrase + "\0", passphrase + "\0").Allowed);
    Expect(!SafetyPolicy.ValidatePassword(passphrase + "\u200B", passphrase + "\u200B").Allowed);
});

var recovery = new RecoveryMaterial
{
    MountPoint = data.MountPoint, VolumeId = data.VolumeId,
    ProtectorId = "ba0987c8-ed31-489f-a5c8-ae1735c7aeba",
    RecoveryPassword = "000011-000022-000033-000044-000055-000066-000077-720885"
};
var scratch = Path.Combine(Path.GetTempPath(), "NordVault.Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
Add("Recovery create-new save and exact roundtrip", () =>
{
    var path = Path.Combine(scratch, "roundtrip.txt");
    var result = RecoveryStore.SaveVerified(path, recovery);
    Expect(result == Path.GetFullPath(path));
    var contents = File.ReadAllText(path);
    Expect(contents.Contains(recovery.RecoveryPassword, StringComparison.Ordinal));
    Expect(contents.Contains(recovery.ProtectorId, StringComparison.Ordinal));
    Expect(contents.Contains(recovery.VolumeId, StringComparison.Ordinal));
    Expect(contents.Contains("ÅTERSTÄLLNINGSNYCKEL", StringComparison.Ordinal));
    Reject(() => RecoveryStore.SaveVerified(path, recovery));
    Expect(File.ReadAllText(path) == contents, "Befintlig återställningsfil ändrades.");
});
Add("Existing unrelated file is preserved", () =>
{
    var path = Path.Combine(scratch, "existing.txt");
    File.WriteAllText(path, "test-owned existing content");
    Reject(() => RecoveryStore.SaveVerified(path, recovery));
    Expect(File.ReadAllText(path) == "test-owned existing content");
});
Add("Recovery same-drive check is case insensitive", () =>
{
    var root = Path.GetPathRoot(scratch)![..2];
    Reject(() => RecoveryStore.SaveVerified(Path.Combine(scratch, "same.txt"), recovery with { MountPoint = root.ToUpperInvariant() }));
    Reject(() => RecoveryStore.SaveVerified(Path.Combine(scratch.ToLowerInvariant(), "same-lower.txt"), recovery with { MountPoint = root.ToUpperInvariant() }));
    Expect(!File.Exists(Path.Combine(scratch, "same.txt")));
});
Add("Network, device, relative and alternate-stream paths rejected", () =>
{
    foreach (var path in new[] { @"\\server\share\key.txt", @"\\?\C:\key.txt", @"\\.\C:\key.txt", "key.txt", "//server/share/key.txt", Path.Combine(scratch, "key.txt:secret") })
        Reject(() => RecoveryStore.SaveVerified(path, recovery));
});
Add("Recovery material must have checksum-valid groups and GUID", () =>
{
    foreach (var password in new[] { "", recovery.RecoveryPassword + "\n", "000012-000022-000033-000044-000055-000066-000077-720885", "000011-000022-000033-000044-000055-000066-000077-720896", "11-22-33-44-55-66-77-88" })
        Reject(() => RecoveryStore.SaveVerified(Path.Combine(scratch, "invalid.txt"), recovery with { RecoveryPassword = password }));
    foreach (var protector in new[] { "", "invalid", Guid.Empty.ToString() })
        Reject(() => RecoveryStore.SaveVerified(Path.Combine(scratch, "invalid.txt"), recovery with { ProtectorId = protector }));
    Reject(() => RecoveryStore.SaveVerified(Path.Combine(scratch, "invalid.txt"), recovery with { VolumeId = " " }));
    Reject(() => RecoveryStore.SaveVerified(Path.Combine(scratch, "invalid.txt"), recovery with { VolumeId = "volume\nmalicious text" }));
    Reject(() => RecoveryStore.SaveVerified(Path.Combine(scratch, "invalid.txt"), recovery with { MountPoint = "Z:\n" }));
    Expect(!File.Exists(Path.Combine(scratch, "invalid.txt")));
});
Add("Missing parent cannot silently save elsewhere", () =>
{
    Reject(() => RecoveryStore.SaveVerified(Path.Combine(scratch, "missing", "key.txt"), recovery));
    Expect(!Directory.Exists(Path.Combine(scratch, "missing")));
});

var failed = 0;
try
{
    foreach (var (name, run) in tests)
    {
        try { run(); Console.WriteLine("PASS " + name); }
        catch (Exception exception) { failed++; Console.Error.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}"); }
    }
}
finally
{
    // Only this unique test directory and files written by these tests are removed.
    foreach (var file in Directory.EnumerateFiles(scratch)) File.Delete(file);
    Directory.Delete(scratch);
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} tests passed. No disk operations were executed.");
return failed == 0 ? 0 : 1;
