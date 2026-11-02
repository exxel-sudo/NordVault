# Källor och kodstudier

Källorna nedan användes för design och kontroll av dokumenterade API:er den 4 oktober 2026. Det är primärkällor från Microsoft, NIST och VeraCrypts eget projekt samt det lokalt bevarade historiska PGP-paketet. De utgör inte en oberoende revision av NordVault.

## Historiskt PGP-paket

- Lokalt arkiv: `pgp263is.tar.gz`.
- Uppackad kod och medföljande dokumentation: `legacy/pgp263i`.
- SHA-256 för den lokala arkivkopian: `0FA4B030596595B732C60667F88C99BA274C882CE2206138F03165378D18430F`.
- Hänvisad spegel från tidigare underlag: [ICM/Sunsite pgp263is.tar.gz](https://sunsite.icm.edu.pl/pub/unix/security/pgpnet/unix/pgp263is.tar.gz).

Den lokala kopians fullständiga ursprungskedja är inte verifierad och PGP-signaturerna är inte kontrollerade mot en betrodd nyckel. Hashen identifierar den här kopian; den bevisar inte vem som skapade den eller att den är autentisk. Originalfilerna bevaras separat och ingår inte i NordVaults kryptografiska motor. De historiska licensuppgifterna och kodfynden redovisas i [LEGACY-REVIEW.sv.md](LEGACY-REVIEW.sv.md).

## Microsoft BitLocker

| Primärkälla | Vad den användes för |
| --- | --- |
| [BitLocker FAQ](https://learn.microsoft.com/en-us/windows/security/operating-system-security/data-protection/bitlocker/faq) | TPM/PIN, datavolymer, upplåsning, återställningsmaterial och skillnaden mellan kryptering och tillfälligt avstängt skydd. |
| [Enable-BitLocker](https://learn.microsoft.com/en-us/powershell/module/bitlocker/enable-bitlocker?view=windowsserver2025-ps) | Parametrar för XTS-AES-256, TPM/PIN, lösenordsskydd och helvolymkryptering utan `UsedSpaceOnly`. Det är Windows som utför kryptografin. |
| [Encrypt: Win32_EncryptableVolume](https://learn.microsoft.com/en-us/windows/win32/secprov/encrypt-win32-encryptablevolume) | `EncryptionFlags = 0x00000002` begär rensning av ledigt utrymme på en krypterad volym som inte redan konverteras eller rensas. Returvärde och aktuell status måste behandlas separat. |
| [GetConversionStatus: Win32_EncryptableVolume](https://learn.microsoft.com/en-us/windows/win32/secprov/getconversionstatus-win32-encryptablevolume) | Separata tillstånd för konvertering och rensning samt procenttal. Ett avrundat värde på 100 procent räcker inte för att styrka tillståndet `FullyEncrypted`. |

API-dokumentation visar hur anrop är avsedda att fungera. Den ersätter inte integrationstester av aktuell Windows-version, firmware, policy eller faktisk enhet.

## Filradering och sanering

[Microsoft Sysinternals SDelete](https://learn.microsoft.com/en-us/sysinternals/downloads/sdelete) beskriver ett separat verktyg för filer och ledigt utrymme, dess flaggor och hur NTFS-fall behandlas. NordVault använder det för den avgränsade HDD-filfunktionen, inte som ett universellt intyg om fysisk sanering. Källan anger begränsningen att gamla filnamn i ledigt utrymme inte rensas. Verktygets licens måste godkännas av användaren; källlänken innebär ingen rätt att omlicensiera det.

[NIST SP 800-88 Rev. 2: Guidelines for Media Sanitization](https://csrc.nist.gov/pubs/sp/800/88/r2/final), publicerad september 2025, användes som underlag för att skilja logisk rensning från sanering av medier och för kravet att metoden måste passa mediet. NordVault är inte certifierat enligt publikationen. Fysisk efterkontroll och enhetsspecifika sanitize-/erase-funktioner ingår inte i förhandsversionen.

## VeraCrypt: studerade moderna kodvägar

VeraCrypt anger själv sina offentliga kodarkiv på [Source Code](https://veracrypt.io/en/Source%20Code.html). Nedan lästes utvalda kodvägar i projektets `master`-gren; grenen kan ändras. Ingen VeraCrypt-kod har kopierats in eller länkats som motor i NordVault. Tabellen redovisar observationer och designlärdomar, inte ett säkerhetsintyg eller påstående om en fullständig revision.

| Kodkälla | Observerad funktion och lärdom |
| --- | --- |
| [Common/Volumes.c](https://github.com/veracrypt/VeraCrypt/blob/master/src/Common/Volumes.c) | `ReadVolumeHeader` och `ReadVolumeHeaderWithAbort` separerar upplåsning av volymhuvudet från användning av datanycklar. Synliga kodvägar hanterar lösenordsbaserad härledning, kontrollfält och städning av nyckelbuffertar. Lärdom: återställning, avbrott och nyckellivslängd är egna säkerhetskrav. |
| [Common/Xts.c](https://github.com/veracrypt/VeraCrypt/blob/master/src/Common/Xts.c) | `EncryptBufferXTS` och de parallella varianterna hanterar dataenhetsnummer, blockpositioner och två nyckelscheman. Lärdom: sektorkryptering har andra krav än PGP:s sekventiella fil-CFB och bör läggas i en etablerad motor. |
| [Common/Random.c](https://github.com/veracrypt/VeraCrypt/blob/master/src/Common/Random.c) | `RandgetBytesFull` använder en synkroniserad pool och kontroll av slow-/fast-pollresultat. Lärdom: fel i insamling eller initialisering måste märkas; tidsvärden eller filstorlek ska inte förväxlas med en säker nyckelkälla. |
| [Common/Tests.c](https://github.com/veracrypt/VeraCrypt/blob/master/src/Common/Tests.c) | `XTSAesTest` jämför krypterad utdata med kända XTS-vektorer; `TestSectorBufEncryption` övar dataenhetspositioner och sektorbuffertar. Lärdom: jämförelse med oberoende testvektorer ger mer än att bara kryptera och dekryptera med samma implementation. |
| [Common/Wipe.c](https://github.com/veracrypt/VeraCrypt/blob/master/src/Common/Wipe.c) | `GetWipePassCount` och `WipeBuffer` väljer mönster och antal pass; slumpfyllning kan delegeras till anroparen. Detta är buffert- och mönsterkod, inte i sig bevis för att en SSD:s reservceller saneras. Lärdom: många pass är ingen ersättning för rätt metod och verifiering. |

VeraCrypt har sina egna licensvillkor och en större systemarkitektur med drivrutin och förstartskod. Att dess källkod är läsbar innebär inte att samma egenskaper automatiskt finns i det här projektet.

## GnuPG och filmodulens design

| Primärkälla | Användning |
| --- | --- |
| [GnuPG nedladdningar](https://gnupg.org/download/index.html) och [Gpg4win](https://www.gpg4win.org/download.html) | Val av GnuPG 2.5.24 från Gpg4win 5.1.1 för detta fasta bygge. |
| [Gpg4win paketintegritet](https://www.gpg4win.org/package-integrity.html) | Publicerad SHA-256 och signeringscertifikat för det hämtade paketet. |
| [GnuPG OpenPGP Options](https://gnupg.org/documentation/manuals/gnupg/OpenPGP-Options.html) | AES-val, `--use-ocb-sym` och saltad, itererad S2K. |
| [GnuPG Esoteric Options](https://gnupg.org/documentation/manuals/gnupg/GPG-Esoteric-Options.html) | Lösenord via fd, batch/loopback och avstängd symmetrisk lösenordscache. |
| [GnuPG Input and Output](https://gnupg.org/documentation/manuals/gnupg/GPG-Input-and-Output.html) | Standardutdata, chunkstorlek och utdatahantering. |
| [GnuPG status-API, doc/DETAILS](https://github.com/gpg/gnupg/blob/master/doc/DETAILS) | `BEGIN_ENCRYPTION`, `DECRYPTION_INFO`, avsluts- och felstatusar; inga lösenord behöver läggas i argument. |
| [RFC 7253](https://datatracker.ietf.org/doc/html/rfc7253) | OCB:s säkerhetsantaganden, särskilt kravet att inte återanvända nonce under samma nyckel. |

Leverantörens källkodspaket och licensinformation länkas i `tools/gnupg/ORIGIN.txt` och medföljande `LICENSES.txt`. Ingen egen implementation av AES, OCB eller S2K har lagts till.

## Portabel distribution, valv och Windows 11 — kontroll 5 oktober 2026

- [Microsoft .NET single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview): fristående paketering för bestämt OS/arkitektur, medföljande runtime, nativebibliotekens temporära uppackning, `AppContext.BaseDirectory` och `Environment.ProcessPath`. Komprimering minskar paketstorleken men kostar arbete vid start. Den lokala publiceringen använder win-x64 och runtime 10.0.11.
- [Microsoft BitLocker overview](https://learn.microsoft.com/en-us/windows/security/operating-system-security/data-protection/bitlocker/): tillgängliga Windows-utgåvor och plattformsberoenden. Detta installerar inte BitLocker i en Windows-utgåva som saknar funktionen.
- Lokal källkod: `VaultSession`, `VaultCatalog`, `ArchivePackage`, `SafeZip`, `DeploymentService` och de nya WPF-apparna `NordVault.Reader`/`NordVault.Setup`. Konstruktionen är egen, med GnuPG som chiffermotor. Inga nya chiffer eller en egen diskdrivrutin har implementerats.
- Lokal reproducerbar mätkod: `tests/NordVault.Benchmark`. Resultat och metod i [PERFORMANCE.sv.md](PERFORMANCE.sv.md); mätningen gäller utvecklingsdatorn och ersätter inte jämförande prestanda- eller säkerhetsprov.

## Vad som ännu inte verifierats

Den 7 oktober 2026 kontrollerades [Microsoft Shell Links](https://learn.microsoft.com/en-us/windows/win32/shell/links) och [IShellLinkW](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ishelllinkw) för skapande, persistens, mål, argument, ikon och läsning av Windows-genvägar. NordVault 0.3.1 använder Windows COM-gränssnitten IShellLinkW/IPersistFile och provar verkliga `.lnk`-filer i testmappar.

Inga källor ovan styrker påståendet att NordVault är buggfritt eller bättre än Symantec PGP Whole Disk Encryption. Det krävs en definierad jämförelse, egna mätningar, integrationstester och oberoende granskning. Dokumentets konkreta begränsningar finns i [SECURITY.sv.md](SECURITY.sv.md).
