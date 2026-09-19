# LexiSharp — conventions de travail

- **Commit** après chaque *feature importante complète* : build sans warning
  (`dotnet build LexiSharp.slnx -c Release`) puis tests verts
  (`dotnet test tests/LexiSharp.Tests -c Release`) avant de committer.
- Messages de commit en **anglais**, une feature = un commit, préfixe
  conventionnel (`feat:`, `fix:`, `refactor:`, `chore:`, ...).
- La solution est `LexiSharp.slnx` (format .NET 10, XML) : ne pas régénérer de `.sln`.
- Cible net10.0. Packages optionnels : `LexiSharp.Postgres` (Npgsql),
  `LexiSharp.ParadeDB` (BM25 Tantivy via pg_search, référence LexiSharp.Postgres),
  `LexiSharp.Hybrid` (fédération + RRF),
  `LexiSharp.MessagePack` (persistance binaire de l'index in-memory, MessagePack + LZ4)
  — ne pas ajouter de dépendances au core.
  La couture `IEmbeddingProvider` vit **dans le core** (partagée Hybrid/Postgres) ;
  Postgres ne référence jamais Hybrid.
- Tests d'intégration Postgres désactivés sauf si `POSTGRES_TEST_CONNECTION` pointe
  vers une instance joignable : `pgvector/pgvector:pg16` couvre lexical+vectoriel+sparse+fuzzy
  (extensions `vector` et `pg_trgm`/`fuzzystrmatch`), `paradedb/paradedb:pg16` couvre
  lexical+ParadeDB+fuzzy (extension `pg_search`, auto-skip si absente) — `postgres:16`
  ne couvre que le lexical. Les tests fuzzy s'auto-skippent si `pg_trgm` manque ; les tests
  vectoriel/sparse s'auto-skippent si l'extension `vector` manque.
- CI (`.github/workflows/`) : `ci.yml` (build+tests, gate de couverture 60 % total line
  via coverlet.msbuild, SonarCloud auto-skip sans `SONAR_TOKEN`, gitleaks, license-check,
  SBOM SPDX, smoke-bench), `codeql.yml` (csharp hebdo), `publish.yml` (tag `v*` → gate CI
  verte → pack + push nuget.org via OIDC + GitHub Release, core uniquement).
  Actions épinglées par SHA de commit ; Dependabot met à jour nuget + github-actions.
- Baseline API publique (drift check) : prévue pour le jalon 1.0, pas avant —
  l'API bouge trop en 0.x pour que la friction soit rentable.