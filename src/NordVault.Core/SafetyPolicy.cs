using System.Text.RegularExpressions;
using System.Globalization;

namespace NordVault.Core;

/// <summary>Fail-closed checks used before disk operations. The backend must recheck current state.</summary>
public static class SafetyPolicy
{
    private static readonly Regex MountPointPattern = new(@"\A[A-Z]:\z", RegexOptions.CultureInvariant);
    private static readonly string[] DataFileSystems = ["NTFS", "FAT", "FAT16", "FAT32", "exFAT"];

    public static PolicyDecision Encrypt(VolumeInfo volume, DiscoveryResult environment)
    {
        var common = ValidateVolume(volume, environment);
        if (!common.Allowed) return common;
        if (!Is(volume.LockStatus, "Unlocked"))
            return PolicyDecision.Deny("Enheten måste vara upplåst före kryptering.");
        if (!Is(volume.VolumeStatus, "FullyDecrypted"))
            return PolicyDecision.Deny("Enheten måste vara helt okrypterad. En pågående eller pausad åtgärd får inte ersättas.");
        if (!Is(volume.ProtectionStatus, "Off"))
            return PolicyDecision.Deny("BitLockers skyddsstatus är oväntad. Läs in enhetens status igen.");
        if (volume.WipingStatus is not (0 or 1))
            return PolicyDecision.Deny("Volymens rensningsstatus måste vara känd och inaktiv före kryptering.");
        if (Is(volume.VolumeType, "OperatingSystem"))
        {
            if (!Is(volume.DriveType, "Fixed") || !Is(volume.FileSystem, "NTFS"))
                return PolicyDecision.Deny("Systemdisken måste vara en fast NTFS-enhet.");
            if (!environment.TpmReady)
                return PolicyDecision.Deny("Systemdiskens kryptering kräver en aktiverad och färdig TPM.");
            return PolicyDecision.Allow("Systemdisken kan krypteras med TPM och PIN samt en verifierad återställningsnyckel.");
        }
        if (!IsDataVolume(volume))
            return PolicyDecision.Deny("Endast identifierade dataenheter och systemdisken kan krypteras.");
        if (!DataFileSystems.Any(fileSystem => Is(volume.FileSystem, fileSystem)))
            return PolicyDecision.Deny("Dataenheten måste använda NTFS, FAT, FAT16, FAT32 eller exFAT.");
        return PolicyDecision.Allow("Dataenheten kan krypteras efter att lösenord och återställningsnyckel har verifierats.");
    }

    public static PolicyDecision WipeFreeSpace(VolumeInfo volume, DiscoveryResult environment)
    {
        var common = ValidateVolume(volume, environment);
        if (!common.Allowed) return common;
        if (!Is(volume.LockStatus, "Unlocked"))
            return PolicyDecision.Deny("Enheten måste vara upplåst före rensning av ledigt utrymme.");
        if (!Is(volume.VolumeStatus, "FullyEncrypted") || !Is(volume.ProtectionStatus, "On"))
            return PolicyDecision.Deny("Rensning kräver en helt krypterad enhet med BitLocker-skyddet påslaget.");
        if (volume.WipingStatus is 2 or 3)
            return PolicyDecision.Deny("En rensning av ledigt utrymme pågår eller är pausad. Starta ingen ny rensning.");
        if (volume.WipingStatus is < 0 or > 3)
            return PolicyDecision.Deny("Rensningens status är okänd. Läs in enhetens status igen.");
        if (!Is(volume.MediaKind, "HDD"))
            return PolicyDecision.Allow("BitLocker kan rensa logiskt ledigt utrymme. På SSD eller okänd lagring kan gamla data finnas i reservblock, snapshots eller andra kopior; fysisk radering garanteras inte.");
        return PolicyDecision.Allow("BitLocker kan rensa ledigt utrymme. Säkerhetskopior, snapshots och otillgängliga eller ommappade sektorer omfattas inte.");
    }

    public static PolicyDecision ShredFile(VolumeInfo volume, DiscoveryResult environment)
    {
        var common = ValidateVolume(volume, environment);
        if (!common.Allowed) return common;
        if (!Is(volume.MediaKind, "HDD"))
            return PolicyDecision.Deny("Filöverskrivning är endast tillåten på en identifierad HDD. SSD, flash och okänd lagring kan behålla tidigare data i reservblock; säker radering kan inte garanteras.");
        if (!Is(volume.LockStatus, "Unlocked"))
            return PolicyDecision.Deny("Enheten måste vara upplåst före filöverskrivning.");
        if (!Is(volume.FileSystem, "NTFS"))
            return PolicyDecision.Deny("Filöverskrivning kräver NTFS för att kunna kontrollera länkar och filattribut.");
        if ((!Is(volume.VolumeStatus, "FullyDecrypted") && !Is(volume.VolumeStatus, "FullyEncrypted")) || volume.WipingStatus is not (0 or 1))
            return PolicyDecision.Deny("Filförstöring kräver en inaktiv volym med känd krypterings- och rensningsstatus.");
        return PolicyDecision.Allow("Valda vanliga filer på HDD kan skrivas över och tas bort. Snapshots, säkerhetskopior, gamla filversioner och ommappade sektorer omfattas inte.");
    }

    public static PolicyDecision ValidatePassword(string password, string confirmation)
    {
        if (string.IsNullOrWhiteSpace(password))
            return PolicyDecision.Deny("Lösenordet får inte vara tomt eller bara innehålla blanksteg.");
        if (password.Length < 14)
            return PolicyDecision.Deny("Använd minst 14 tecken i lösenordet.");
        if (password.Length > 256)
            return PolicyDecision.Deny("Lösenordet får innehålla högst 256 tecken.");
        if (password.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
            return PolicyDecision.Deny("Lösenordet får inte innehålla kontrolltecken eller radbrytningar.");
        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
            return PolicyDecision.Deny("Lösenorden måste vara exakt likadana.");
        return PolicyDecision.Allow("Lösenordet uppfyller minimikraven. En lång, unik lösenfras rekommenderas.");
    }

    private static PolicyDecision ValidateVolume(VolumeInfo? volume, DiscoveryResult? environment)
    {
        if (environment is null || !environment.IsAdministrator)
            return PolicyDecision.Deny("Åtgärden kräver administratörsbehörighet.");
        if (!environment.BitLockerAvailable)
            return PolicyDecision.Deny("BitLocker är inte tillgängligt på den här Windows-installationen.");
        if (volume is null || !MountPointPattern.IsMatch(volume.MountPoint ?? "") || string.IsNullOrWhiteSpace(volume.VolumeId))
            return PolicyDecision.Deny("Enheten saknar en giltig enhetsbokstav eller verifierbar volymidentitet.");
        if (!Is(volume.DriveType, "Fixed") && !Is(volume.DriveType, "Removable"))
            return PolicyDecision.Deny("Endast lokala fasta och flyttbara enheter stöds.");
        return PolicyDecision.Allow("Enheten har en giltig lokal volymidentitet.");
    }

    private static bool IsDataVolume(VolumeInfo volume) =>
        Is(volume.VolumeType, "FixedData") || Is(volume.VolumeType, "RemovableData") || Is(volume.VolumeType, "Data");

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
