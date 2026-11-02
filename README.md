# NordVault

Windows 11-app (WPF, .NET 10) för privat säkerhet på den egna datorn:

- **Diskar och USB** krypteras med BitLocker, rensning av ledigt utrymme
- **Filkryptering** med GnuPG (AES-256/OCB) och **självuppackande arkiv** (.exe med lösenord, även hela mappar)
- **Valv** för filer, lösenord, länkar, anteckningar och kopplade konton – låst med huvudlösenord, återställningskoder och Windows Hello
- **Firefox-tillägg** som frågar om att spara lösenord och fyller i dem efter bekräftelse i NordVault
- **Högerklick i Utforskaren:** skapa självuppackande arkiv och rensa ledigt utrymme (1, 3 eller 7 pass)

Hela beskrivningen finns i [README.sv.md](README.sv.md) och säkerhetsmodellen i [docs/SECURITY.sv.md](docs/SECURITY.sv.md).

## Licens

[GPL-3.0](LICENSE). Microsoft SDelete ingår inte och laddas ner separat.
