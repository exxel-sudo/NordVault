# Lokal prestandamätning — NordVault 0.3.0

Mätt 5 oktober 2026 på Intel Core i7-7820X 3,60 GHz (8 kärnor, 16 logiska processorer), cirka 64 GiB RAM, Windows 11 Enterprise build 26200, projektets lokala E:-lagring. Koden i `tests/NordVault.Benchmark` skapar och tar endast bort egna testfiler. Ingen riktig disk krypteras eller rensas.

| Åtgärd | Tid | Resultathastighet |
| --- | ---: | ---: |
| Kryptera 64 MiB slumpad testfil | 1,099 s | 58,2 MiB/s |
| Verifiera och dekryptera samma fil | 1,323 s | 48,4 MiB/s |
| Kryptera liten metadatafil | 0,591 s | Start/härledning dominerar |

Original och återställd fil jämfördes med SHA-256 och var identiska. Metoden är **en varm provkörning**, efter en liten uppvärmningsoperation, med sannolik filcache. Tiderna omfattar appens motorverifiering, process-/agentstart, lösenordshärledning, strömmad behandling och flush. Dekryptering inkluderar första autentiseringsgenomgången och andra genomgången som skriver resultatet. Den avsiktliga extra verifieringen ingår alltså i siffrorna.

Mätningen anger genomströmning för den ursprungliga datamängden, inte all fysisk I/O. Den är inte en statistisk benchmark, kallstartsmätning, rådiskmätning, USB-prov eller BitLocker-prov. Ingen SSD-modell, diskkontroller eller exakt CPU-frekvens under körningen har kvalificerats här. Inga slutsatser om Symantecs relativa hastighet kan dras.

Många små filer blir långsammare per byte eftersom varje fil har separat lösenordshärledning och motoroperation; valvimport uppdaterar dessutom krypterad katalog. En stor fil kan begränsas av lagring eller CPU. USB-hastighet, cache, antivirus, andra arbetsuppgifter och strömläge påverkar resultatet. Appen använder 64 KiB-streaming och asynkrona filoperationer, men detta är inte en utfästelse om optimal fart på varje Windows 11-dator.

För att återupprepa från projektmappen:

```powershell
dotnet run --project .\tests\NordVault.Benchmark --configuration Release
```

Råresultatet skrivs till `artifacts/performance-0.3.0.json`. Upprepade prov ska redovisa hela fördelningen och om samma lagring/cache användes. Krypterade valv/arkiv och BitLocker behöver egna mätningar innan siffrorna tillämpas på dem.

## Diskinventering i 0.3.2

Den 7 oktober 2026 kördes skrivskyddad Discover utan admin på samma dator, med sex visade volymer. Den tidigare backendfilen tog **36,28 och 35,75 sekunder**; den ändrade filen tog **5,63 och 5,50 sekunder**. Ordningen var gammal, ny, gammal, ny. Windows/process-/modulstart och avläsning ingår. Detta är två lokala prov per version med möjlig cache, inte en statistisk benchmark eller ett prov med admin/BitLocker-status. Råresultat finns i `artifacts/disk-discovery-performance-0.3.2.json`.

I denna begränsade testsession returnerades säkerhetsstatus och medietyp som Unknown. Ett separat skrivskyddat prov i den vanliga Windows-sessionen utan admin tog **3,49 sekunder** och identifierade samtliga sex volymer som SSD. BitLocker-status var Unknown eftersom privilegierade frågor avsiktligt hoppades över. Avläsning med admin återstår att prova. GUI håller fil- och valvkontroller tillgängliga under väntan, räknar nästa avläsningsintervall från slutförd kontroll och visar separat status i stället för den globala arbetsindikatorn.
