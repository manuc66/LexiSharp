# LexiSharp — conventions de travail

- **Commit** après chaque *feature importante complète* : build sans warning
  (`dotnet build LexiSharp.slnx -c Release`) puis tests verts
  (`dotnet test tests/LexiSharp.Tests -c Release`) avant de committer.
- Messages de commit en **anglais**, une feature = un commit, préfixe
  conventionnel (`feat:`, `fix:`, `refactor:`, `chore:`, ...).
- La solution est `LexiSharp.slnx` (format .NET 10, XML) : ne pas régénérer de `.sln`.
- Cible net8.0. Packages optionnels : `LexiSharp.Postgres` (Npgsql),
  `LexiSharp.Hybrid` (fédération + RRF) — ne pas ajouter de dépendances au core.
- Tests d'intégration Postgres désactivés sauf si `POSTGRES_TEST_CONNECTION` pointe
  vers une instance joignable (ex. docker `postgres:16` sur le port 5432).