# ADR 0006 — Retours adaptatifs : fraîcheur, mémoire par contexte et calibration sous dérive

- **Statut** : proposé
- **Date** : 2026-11-25
- **Dépôt** : LexiSharp
- **Série** : ADR LexiSharp n° 0006 ; première décision d'architecture écrite dans ce dépôt.
- **Complément** : `doc/adr/0007-decision-preference.md` (décision et préférence).
- **Portée** : LexiSharp 1.3 (additif), puis validation sur un client de référence.

> Ce fichier vit sous `doc/`, pas sous `docs/` : il n'est pas publié par GitHub Pages. Il est écrit
> pour être collé tel quel dans une issue de design, ou déplacé sous `docs/` si la série ADR doit
> devenir publique.

## Contexte

LexiSharp sait déjà entraîner, prédire, calibrer et chercher. Ce qui manque n'est pas un meilleur
classifieur : c'est une couche qui apprend d'un **flux de retours**, dans le **temps** et par
**contexte**, sans réentraîner le modèle texte à chaque clic.

Le cas d'usage réel est toujours la même forme :

```
texte → catégories proposées → l'utilisateur accepte / corrige / refuse / ignore
```

Tri de mails (contexte expéditeur), routage de tickets (contexte dépôt), tags de fichiers
(contexte dossier), ranking de recherche (contexte requête), gestion de tâches (contexte famille de
titres). La partie texte est déjà couverte par LexiSharp ; la partie « ce que l'utilisateur a fait
de la suggestion, et quand » ne l'est pas.

### Ce que le code fait aujourd'hui

| Brique | État réel | Conséquence |
|---|---|---|
| Poids d'échantillon | `LinearClassifier<TItem>`, `DecisionTreeClassifier<TItem>`, `RandomForestClassifier<TItem>` acceptent `weightOf`. Les façades texte `LinearClassifier`, `DecisionTreeClassifier` et `RandomForestClassifier` passent `weightOf: null` et n'exposent aucun overload. | Un client qui veut des poids est obligé de dupliquer les documents en 1–4 copies, ce qui fausse les comptes, les priors et `Unlearn`. |
| Fraîcheur | `TemporalPrior` compte des affectations par jour/heure, sans décroissance. Aucune primitive `demi-vie + âge maximal` n'existe. | Chaque client réimplémente sa fenêtre et sa demi-vie, souvent avec `DateTime.Now`. |
| Mémoire négative | `IReinforceableTextClassifier` est un ledger additif **global**, indexé par texte + catégorie, sans contexte ni temps. Les façades texte `LinearClassifier`/`DecisionTreeClassifier`/`RandomForestClassifier` ne l'implémentent même pas (elles exposent `Learn`/`Reinforce` avec un token, pas l'interface). | Un refus ne vaut que pour le texte exact, jamais pour la famille ; impossible de dire « ce sujet, dans cette famille, a déjà été refusé trois fois ». |
| Calibration | `ConformalClassifier<TInner>` est un split-conformal **non pondéré** : il exige un jeu de calibration labellisé, calcule un quantile de `1 - p(y|x)` et ne voit que les acceptations. | La couverture moyenne sur 90 jours masque une dérive récente ; un refus ou une abstention n'entre jamais dans la calibration. |
| Évaluation | `OnlineClassificationEvaluator` rejoue un flux labellisé (prédire puis apprendre) avec bootstrap par blocs. | Il ne sait pas mesurer le temps de récupération après une correction, la répétition d'une mauvaise proposition, ni évaluer une politique à partir de logs biaisés (IPS/DR). |
| Temps par échantillon | `SearchDocument` ne porte pas d'horodatage. | Une politique de fraîcheur a besoin de `(document, at)` ; le lien est aujourd'hui une closure ou un dictionnaire externe. |

### Les cinq manques génériques

1. **Poids d'échantillon partout** : les noyaux génériques savent le faire, les façades texte non.
2. **Politique de fraîcheur** : demi-vie, âge maximal, saturation de fréquence, mode exact ou
   quantifié — une primitive, pas une option éparse.
3. **Mémoire de retours par contexte** : acceptations et refus, décroissants, rétrécis vers le
   global, avec veto et purge.
4. **Calibration sous dérive** : quantile conformal pondéré par le temps, couverture qui suit les
   habitudes au lieu de moyenner la fenêtre.
5. **Évaluation hors ligne d'une politique de suggestion** : temps de récupération, regret de
   répétition, couverture réelle vs nominale, IPS/DR quand les logs portent une propension.

## Non-objectifs

- **Pas un nouveau modèle texte.** Aucun embedding, aucun LLM, aucune dépendance NuGet. La couche
  adaptative enveloppe un `ITextClassifier` existant.
- **Pas un remplacement du réentraînement.** Elle corrige le contexte et la calibration entre deux
  réentraînements ; un changement de vocabulaire ou de jeu de labels demande toujours `Train`.
- **Pas un modèle bayésien hiérarchique complet.** Le rétrécissement est une estimation ponctuelle
  (shrinkage), pas une postérieure. C'est le compromis 90 % qui reste explicable et déterministe.
- **Pas d'exploration par défaut.** ε-greedy / Thompson est une option P3 pour les clients
  « ranking » où la politique se fige ; ce n'est pas pour un client où l'utilisateur voit tous les
  retours.
- **Pas un journal d'événements.** La mémoire garde des agrégats, pas les textes. Le client garde
  son log ; LexiSharp ne le remplace pas.
- **Pas multi-tenant.** Une mémoire par tenant (ou des clés préfixées par le client).
- **Pas une garantie de vie privée sur le log.** Le `FeedbackEvent` porte le texte ; c'est le
  client qui le journalise. Le snapshot, lui, ne contient jamais de texte.

## Décision

Ajouter une couche `LexiSharp.Adaptive`, en trois phases livrables séparément :

- **P1 — primitives** : `RecencyPolicy`, `SampleWeightMode`, overloads `weightOf` sur les trois
  façades texte, mode quantifié pour les modèles sans poids.
- **P2 — cœur** : `FeedbackMemory<TContext>`, `AdaptiveFeedbackRanker<TContext>`,
  `WeightedConformalCalibrator`, `AdaptiveEvaluator`, snapshot versionné et signé.
- **P3 — recherche** : `DriftMonitor`, IPS, benchmark de dérive synthétique épinglé.

Le défaut reste le comportement actuel : mémoire vide ⇒ multiplicateur 1, pas de calibration ⇒
pas d'abstention, poids 1 ⇒ modèle identique. Toutes les additions sont compatibles binairement
(voir « Compatibilité »).

## Principe : deux niveaux au-dessus du texte

```
P(c | x, ctx) ∝ p_texte(c | x)
              × (1 + λ · m_ctx(c))          // acceptations décroissantes, rétrécies
              × exp(-γ · r_ctx(c))          // refus décroissants
```

avec :

- `m_ctx(c) ∈ [0, 1]` : part d'acceptation du contexte, rétrécie vers le global ;
- `r_ctx(c) ≥ 0` : masse de refus décroissante pour `(ctx, c)` ;
- `λ, γ, κ` : trois nombres, pas un modèle caché ;
- un `WeightedConformalCalibrator` pour l'abstention, dont le quantile est pondéré par le temps.

Chaque score final expose **trois contributions** : probabilité texte, multiplicateur de contexte,
appartenance au set conformal. C'est la condition pour que la couche reste explicable et
débogable.

## API

### P1 — politique de fraîcheur et poids exacts

```csharp
namespace LexiSharp.Adaptive;

public enum SampleWeightMode
{
    Exact,      // le modèle consomme le poids (défaut)
    Quantized,  // repli : k copies, pour les modèles sans poids
}

public sealed record RecencyPolicy(
    TimeSpan HalfLife,
    TimeSpan MaxAge,
    double FrequencySaturation = 7,
    SampleWeightMode Mode = SampleWeightMode.Exact,
    int MaxCopies = 4)
{
    public static RecencyPolicy None { get; }   // pas de décroissance, pas d'expiration

    public bool IsExpired(DateTimeOffset now, DateTimeOffset at);
    public double Weight(DateTimeOffset now, DateTimeOffset at, int occurrences = 1);
    public int Copies(DateTimeOffset now, DateTimeOffset at, int occurrences = 1);
}
```

Sémantique normative :

- `HalfLife == Timeout.InfiniteTimeSpan` ⇒ pas de décroissance. Sinon `HalfLife > 0`.
- `MaxAge == Timeout.InfiniteTimeSpan` ⇒ pas d'expiration. Sinon `MaxAge > 0`.
- `at > now` ⇒ `ArgumentOutOfRangeException`. Le temps est injecté, jamais lu d'une horloge locale.
- `occurrences >= 1` ⇒ sinon `ArgumentOutOfRangeException`.
- `Weight` :

```text
si expiré (now - at > MaxAge)                       -> 0
décroissance = 0.5 ^ ((now - at) / HalfLife)        // 1 si HalfLife infini
amortissement = 1 / (1 + (occurrences - 1) / FrequencySaturation)
Weight = décroissance * amortissement               // dans [0, 1]
```

`Weight == 0` signifie « hors fenêtre, ne pas entraîner ». Le contrat est donc `[0, 1]`, pas
`(0, 1]` : une politique de fraîcheur doit pouvoir jeter un échantillon.

- `Copies` n'est valide qu'en mode `Quantized` ; sinon `InvalidOperationException`.

```text
si Weight == 0                                      -> 0
Copies = clamp(round(Weight * MaxCopies), 1, MaxCopies)
```

Avec `MaxCopies = 4` : poids 1 → 4 copies, 0,5 → 2, 0,25 → 1. C'est l'échelle 1–4 d'un client qui
quantifie ses poids. Le mode quantifié est une **approximation** : il gonfle les comptes, interagit
avec les priors et avec `Unlearn`. Il n'est là que pour les modèles qui n'ont pas de poids
(NaiveBayes, Centroid, NearestNeighbor), et il doit être documenté comme tel.

```csharp
public readonly record struct TimedDocument(
    SearchDocument Document,
    DateTimeOffset At,
    int Occurrences = 1);

public static class SampleWeighting
{
    public static IEnumerable<SearchDocument> Expand(
        IEnumerable<TimedDocument> samples,
        RecencyPolicy policy,
        DateTimeOffset now);
}
```

**Overloads sur les façades texte.** Les noyaux génériques acceptent déjà `weightOf` ; il suffit de
l'exposer. Attention : ajouter un paramètre optionnel à un constructeur existant **casse la
compatibilité binaire** (la signature disparaît). On ajoute donc des overloads à trois paramètres,
sans toucher aux constructeurs actuels :

```csharp
public LinearClassifier(
    ITokenizer? tokenizer,
    LinearClassifierOptions? options,
    Func<SearchDocument, double> weightOf);

public LinearClassifier(
    IFeatureSource<SearchDocument> features,
    LinearClassifierOptions? options,
    Func<SearchDocument, double> weightOf);

// idem DecisionTreeClassifier et RandomForestClassifier
```

- `weightOf` s'applique à `Train`, et aux `Learn` existants (les noyaux l'utilisent déjà). Un
  `Learn(text, category)` fabrique un `SearchDocument` à `Id` vide : une closure indexée par `Id`
  doit rester totale, ou le client doit passer par `Learn(SearchDocument)`.
- `weightOf` n'est **pas** sérialisé. Un délégué n'est pas une donnée, exactement comme le
  tokenizer ou `IFeatureSource` (`ClassificationModelSerializer` le dit déjà). Les coefficients
  entraînés portent déjà l'effet des poids ; recharger un modèle n'a pas besoin du délégué.
- Ce qui se sérialise, c'est la **politique** (`RecencyPolicy`, un record de données), dans le
  snapshot adaptatif P2. Ajouter un hash de politique à `LXCL` serait un bump de format pour rien.

### P2 — mémoire, façade, calibration

```csharp
namespace LexiSharp.Adaptive;

public enum FeedbackOutcome
{
    Accepted,   // la proposition était bonne
    Corrected,  // l'utilisateur a choisi autre chose
    Rejected,   // la proposition est mauvaise, sans remplacement
    Abstained,  // le système n'a rien proposé
    Ignored,    // l'utilisateur a vu et n'a rien fait
}

public sealed record FeedbackEvent<TContext>(
    string Text,
    string? OfferedCategory,
    string? AcceptedCategory,
    TContext Context,
    DateTimeOffset At,
    FeedbackOutcome Outcome,
    double? Propensity = null,                       // P3 : probabilité du log
    IReadOnlyList<ClassificationResult>? Offered = null) // scores montrés, pour la calibration
    where TContext : notnull;
```

`Offered` et `Propensity` sont optionnels mais **nécessaires** à deux fonctions : sans `Offered`,
le calibrateur ne peut pas calculer `1 - p(actual)` ; sans `Propensity`, IPS ne peut pas corriger
le biais. Les deux sont donc dans l'événement de log, pas dans une structure séparée.

Validation stricte dans `Observe` (un log incohérent est un bug, pas une donnée à normaliser) :

| `Outcome` | `OfferedCategory` | `AcceptedCategory` | Effet mémoire |
|---|---|---|---|
| `Accepted` | non nul | égal à `Offered` | +1 acceptation pour `Accepted` ; refus de `Offered` diminué de 1 (plancher 0) |
| `Corrected` | non nul | non nul, différent | +1 acceptation pour `Accepted` ; +1 refus pour `Offered` |
| `Rejected` | non nul | nul | +1 refus pour `Offered` |
| `Ignored` | non nul | nul | exposition seulement ; aucune masse |
| `Abstained` | nul | nul ou label | si label : +1 acceptation pour le label ; pas d'exposition |

`At` doit être non décroissant par mémoire. `Propensity`, si présent, est dans `(0, 1]`, et nul pour
`Abstained`. `Offered`, si présent, contient `OfferedCategory`, avec des probabilités finies dans
`[0, 1]`.

```csharp
public interface IContextCodec<TContext> where TContext : notnull
{
    string Id { get; }          // identité stable et versionnée du codec
    bool IsOpaque { get; }      // true : la clé ne révèle pas le contexte
    string Encode(TContext context);
}

public static class ContextCodecs
{
    public static IContextCodec<TContext> Hashed<TContext>() where TContext : notnull;
    public static IContextCodec<TContext> Raw<TContext>() where TContext : notnull; // opt-in
}

public sealed record FeedbackMemoryOptions
{
    public int VetoAfter { get; init; } = 3;
    public int MaxContexts { get; init; } = 100_000;
    public int MaxCategoriesPerContext { get; init; } = 64;
    public long MaxPairs { get; init; } = 1_000_000;
    public double MinMass { get; init; } = 1e-4;
    public double Shrinkage { get; init; } = 10;     // κ, en unités de retour
    public double GlobalSmoothing { get; init; } = 0.5; // α₀
}

public sealed class FeedbackMemory<TContext> where TContext : notnull
{
    public FeedbackMemory(
        RecencyPolicy policy,
        FeedbackMemoryOptions? options = null,
        IContextCodec<TContext>? codec = null); // défaut : ContextCodecs.Hashed<TContext>()

    public RecencyPolicy Policy { get; }
    public int ContextCount { get; }

    public void Observe(FeedbackEvent<TContext> feedback);

    public ContextPrior Prior(TContext context, DateTimeOffset now);
    public IReadOnlySet<string> Vetoed(TContext context, DateTimeOffset now);

    public void Purge(DateTimeOffset now);
    public void Prune(IReadOnlySet<string> categories);
    public void Rebase(DateTimeOffset now, RecencyPolicy policy);

    public FeedbackSnapshot Save(DateTimeOffset now);
    public static FeedbackMemory<TContext> Load(
        FeedbackSnapshot snapshot,
        IContextCodec<TContext>? codec = null);
}
```

Le codec par défaut **hache** : SHA-256 tronqué à 128 bits, hexadécimal minuscule, avec un `Id` du
type `sha256-128:v1:<nom du type>`. Le snapshot ne contient donc aucune clé de contexte en clair,
et `Load` refuse un codec dont l'`Id` diffère. `ContextCodecs.Raw` est l'opt-in explicite, pour le
débogage.

`FeedbackSnapshot` est une projection **matérialisée à un instant** : les masses stockées sont
« à la dernière mise à jour », et `Save(now)`/`Prior(now)` les projettent par décroissance sans
muter l'état vivant. C'est ce qui permet des lectures concurrentes pures.

```csharp
public sealed record CategoryPrior(
    string Category,
    double AcceptanceShare,   // m_ctx(c), dans [0, 1]
    double GlobalShare,       // a_global(c), pour l'explication
    double AcceptanceMass,    // A_ctx(c) projetée
    double RefusalMass);      // r_ctx(c) projetée

public sealed record ContextPrior(
    IReadOnlyList<CategoryPrior> Categories, // seulement les catégories avec de l'évidence
    double Evidence,                         // T_ctx / (T_ctx + κ), dans [0, 1)
    double ResponseRate,                     // réponses / expositions, dans [0, 1]
    long Observations,
    DateTimeOffset LastUpdated);

public sealed record CategoryFeedbackSnapshot(
    string Category,
    double AcceptanceMass,
    double RefusalMass);

public sealed record GlobalFeedbackSnapshot(
    DateTimeOffset LastUpdated,
    IReadOnlyList<CategoryFeedbackSnapshot> Categories);

public sealed record ContextFeedbackSnapshot(
    string Key,
    DateTimeOffset LastUpdated,
    long Exposures,
    long Responses,
    long Observations,
    IReadOnlyList<CategoryFeedbackSnapshot> Categories);

public sealed record FeedbackSnapshot(
    int SchemaVersion,
    string CodecId,
    RecencyPolicy Policy,
    FeedbackMemoryOptions Options,
    bool ContextsHashed,
    DateTimeOffset SavedAt,
    GlobalFeedbackSnapshot Global,
    IReadOnlyList<ContextFeedbackSnapshot> Contexts);
```

`Prior` ne renvoie **pas** une distribution normalisée : c'est de l'évidence par catégorie. Le
classifieur normalise après multiplication. Une catégorie absente de `Prior` a
`m = 0, r = 0` et un multiplicateur de 1.

`FeedbackSnapshot` est un DTO en mémoire, versionné ; la persistance passe par `AdaptiveSnapshot`
(`LXFA`), qui porte la signature. `FeedbackMemory.Save/Load` sert aux tests et aux clients qui
veulent sérialiser la mémoire eux-mêmes.

```csharp
public sealed record AdaptiveOptions
{
    public double AcceptanceWeight { get; init; } = 0.5;          // λ
    public double RefusalWeight { get; init; } = Math.Log(2.0);  // γ : un refus ≈ ×0,5
    public double MinScore { get; init; } = 0;                   // abstention basse, 0 = jamais
    public bool AutoTuneHalfLife { get; init; }                  // P3, défaut false
}
```

```csharp
public interface IAdaptiveFeedbackPolicy<TContext> where TContext : notnull
{
    AdaptiveRanking Predict(string text, TContext context, DateTimeOffset now, int limit = 6);
    void Observe(FeedbackEvent<TContext> feedback);
}

public sealed class AdaptiveFeedbackRanker<TContext> : IAdaptiveFeedbackPolicy<TContext>
    where TContext : notnull
{
    public AdaptiveFeedbackRanker(
        ITextClassifier model,
        FeedbackMemory<TContext> memory,
        AdaptiveOptions? options = null,
        WeightedConformalCalibrator? calibrator = null);

    public ITextClassifier Model { get; }
    public FeedbackMemory<TContext> Memory { get; }
    public long MissingOfferedScores { get; }

    public AdaptiveRanking Predict(string text, TContext context, DateTimeOffset now, int limit = 6);
    public AdaptiveExplanation Explain(string text, TContext context, DateTimeOffset now, int limit = 6);
    public void Observe(FeedbackEvent<TContext> feedback);
    public void Rebase(DateTimeOffset now, RecencyPolicy policy); // P3

    public AdaptiveSnapshot Save(DateTimeOffset now, string modelSignature);
    public static AdaptiveFeedbackRanker<TContext> Load(
        AdaptiveSnapshot snapshot,
        ITextClassifier model,
        IContextCodec<TContext>? codec = null);
}

public sealed record AdaptiveScore(
    string Category,
    double TextProbability,
    double AcceptanceShare,
    double RefusalMass,
    double ContextMultiplier,
    double FinalProbability,
    bool Vetoed,
    bool InPredictionSet);

public sealed record AdaptiveRanking(
    IReadOnlyList<AdaptiveScore> Results,     // top `limit`
    IReadOnlyList<AdaptiveScore> AllScores,   // liste complète post-veto, pour le log/calibration
    PredictionSet? PredictionSet,             // null si pas de calibrateur
    bool Abstained,
    bool VetoFallback,
    double ContextEvidence,
    DateTimeOffset At);

public sealed record AdaptiveExplanation(
    string Text,
    DateTimeOffset At,
    ContextPrior Prior,
    IReadOnlyList<AdaptiveScore> Scores,
    ClassificationExplanation? TextExplanation);

public sealed record AdaptiveSnapshot(
    int SchemaVersion,
    string ModelSignature,             // fournie par l'appelant (hash du LXCL, config, …)
    AdaptiveOptions Options,
    FeedbackSnapshot Memory,
    WeightedConformalSnapshot? Calibration,
    string Signature);                 // SHA-256 sur le corps canonique

public static class AdaptiveSnapshotSerializer
{
    public static void Save(AdaptiveSnapshot snapshot, Stream destination);
    public static AdaptiveSnapshot Load(Stream source);
}
```

Le nom `AdaptiveFeedbackRanker` est volontaire : `AdaptiveClassifier` est **déjà pris** par le
wrapper de swap atomique (`TrySwap`, `TrySwapSignificant`). Renommer l'existant serait une
rupture 1.x ; les deux concerns sont complémentaires (le ranker peut envelopper un
`AdaptiveClassifier` comme modèle texte).

`Predict` ne met **pas** à jour le modèle texte. Il applique la mémoire et la calibration ; le
modèle reste celui du dernier `Train`/`Learn` du client. C'est volontaire : mettre à jour le texte
et la mémoire sur le même événement double le signal et rend l'évaluation impossible à attribuer.

#### Calcul normatif d'une prédiction

1. `textResults = model.Predict(text, limit: int.MaxValue)` — sans `excludedCategories`, pour
   garder la distribution intacte.
2. `prior = memory.Prior(context, now)`.
3. Pour chaque catégorie offerte par le modèle :

```text
A = prior.AcceptanceMass(c)        (0 si absente)
R = prior.RefusalMass(c)           (0 si absente)
m = prior.AcceptanceShare(c)
multiplier = (1 + λ·m) · exp(-γ·R)
score = p_texte(c) · multiplier
```

4. Veto : retirer les catégories avec `R >= vetoAfter`. Si **toutes** sont vetoées, garder la liste
   complète, poser `VetoFallback = true` et ne pas renvoyer un classement vide.
5. Normaliser `score` sur les survivantes. Trier par score décroissant, avec l'ordre du modèle
   comme tie-break stable.
6. Si un calibrateur est présent : `set = calibrator.PredictSet(scores, now, topK: limit)`.
   `Abstained = set.IsEmpty || top.Score < MinScore`. Sinon `Abstained = top.Score < MinScore`.
7. `ContextEvidence = prior.Evidence`.

Cas d'identité : mémoire vide, aucun veto, aucun calibrateur ⇒ renvoyer directement les résultats
du modèle, mêmes probabilités et même ordre. Une voie rapide doit garantir l'égalité **bit à bit**
quand aucune renormalisation n'est nécessaire.

`Observe` :

1. Si `Outcome` est `Accepted` ou `Corrected` et `feedback.Offered != null` :
   `calibrator.Observe(feedback.At, feedback.Offered, feedback.AcceptedCategory!)`. Sinon, si
   `Offered` est nul, incrémenter `MissingOfferedScores` (le calibrateur ne peut pas apprendre de
   cet événement).
2. `memory.Observe(feedback)`.
3. Si `AutoTuneHalfLife`, `DriftMonitor` (P3) est mis à jour ; voir plus bas.

#### Mémoire : formules exactes

Pour un contexte `ctx` et une catégorie `c`, on stocke deux masses et un horodatage de dernière
mise à jour :

- `A_ctx(c)` : acceptations décroissantes ;
- `R_ctx(c)` : refus décroissants ;
- `T_ctx = Σ_c A_ctx(c)`.

Le global ne stocke que les acceptations (`A_global(c)`, `T_global`). Les refus sont
volontairement locaux : un refus dans une famille ne doit pas disqualifier la catégorie partout.

```text
a_global(c) = (A_global(c) + α₀) / (T_global + K·α₀)   si K > 0, sinon 0
m_ctx(c)    = (A_ctx(c) + κ·a_global(c)) / (T_ctx + κ)
Evidence    = T_ctx / (T_ctx + κ)
```

où `K` est le nombre de catégories ayant au moins une acceptation globale et `α₀ =
GlobalSmoothing`. `m_ctx` est exactement le shrinkage `(n·m_ctx + κ·m_global)/(n+κ)` de la
proposition, écrit avec `n = T_ctx`.

Décroissance : chaque masse est multipliée par `0.5 ^ (dt / HalfLife)` entre deux accès. En
pratique :

- `Observe` matérialise le contexte et le global à `feedback.At` (O(catégories)), puis ajoute 1 ;
- `Prior`/`Vetoed`/`Save` calculent la projection à `now` **sans muter** l'état ;
- `Purge(now)` supprime les contextes dont toutes les masses projetées sont `< MinMass`, et
  applique les caps LRU ;
- `Rebase(now, policy)` matérialise à `now` sous l'ancienne politique, puis remplace la politique.
  Changer de demi-vie ne réécrit pas le passé de la mémoire ; le calibrateur, lui, repondère ses
  entrées à chaque `PredictSet` avec la politique courante (voir plus bas).
  `AdaptiveFeedbackRanker.Rebase` met à jour la mémoire **et** reconstruit le calibrateur avec la
  même politique ; sinon les deux fenêtres divergent silencieusement.

Confirmation : `Accepted` fait `R = max(0, R - 1)`. Un refus récent est effacé par une
acceptation ; un refus ancien a déjà décru et ne compte plus.

Veto : `R_ctx(c) >= VetoAfter`, avec `VetoAfter >= 1`. Le veto est **doux** : il retire la
catégorie de la proposition, il ne détruit pas sa mémoire. Il décroît donc tout seul, et une
acceptation peut le lever.

#### Calibration conformal pondérée

```csharp
public sealed class WeightedConformalCalibrator
{
    public WeightedConformalCalibrator(
        double alpha,
        RecencyPolicy policy,
        int minSamples = 20,
        int maxSamples = 10_000,
        int refreshEvery = 64,
        TimeSpan? maxThresholdAge = null);

    public double Alpha { get; }
    public RecencyPolicy Policy { get; }
    public bool IsCalibrated { get; }
    public double Threshold { get; }   // 1 - q, NaN avant le premier calcul
    public int Count { get; }

    public void Observe(
        DateTimeOffset at,
        IReadOnlyList<ClassificationResult> offered,
        string actual);

    public PredictionSet PredictSet(
        IReadOnlyList<ClassificationResult> offered,
        DateTimeOffset now,
        int? topK = null);

    public void Purge(DateTimeOffset now);
    public void Reset();

    public WeightedConformalSnapshot Save(DateTimeOffset now);
    public static WeightedConformalCalibrator Load(WeightedConformalSnapshot snapshot);
}

public readonly record struct CalibrationEntry(double Score, DateTimeOffset At);

public sealed record WeightedConformalSnapshot(
    int SchemaVersion,
    DateTimeOffset SavedAt,
    double Alpha,
    RecencyPolicy Policy,
    int MinSamples,
    int MaxSamples,
    int RefreshEvery,
    TimeSpan MaxThresholdAge,
    IReadOnlyList<CalibrationEntry> Entries);
```

Score de non-conformité : `s_i = 1 - p_i(actual)` (0 si la catégorie réelle est absente de
`offered`). Les entrées sont stockées `(s_i, at_i)`, pas avec un poids figé : c'est `PredictSet`
qui recalcule `w_i = policy.Weight(now, at_i)` avec l'instant courant. Une calibration qui vieillit
se repondère donc toute seule.

Quantile pondéré, écrit pour **coïncider exactement** avec `ConformalClassifier` quand tous les
poids valent 1 :

```text
W      = Σ w_i
target = (1 - α) · (W + 1)       // +1 = le point de test, de poids 1
trier les s_i croissants
cum = 0
q = max(s_i)
pour chaque i : cum += w_i ; si cum >= target { q = s_i ; break }
Threshold = 1 - q
```

Justification du `+1` : sans pondération, `W = n` et `target = (1-α)(n+1)`, donc l'indice est
`ceil((n+1)(1-α)) - 1`, exactement la formule actuelle. Si `target > W`, `q` reste le score
maximal (on inclut tout) ; si `W = 0` (tout est expiré), le calibrateur n'est pas calibré et on
retombe sur le seuil `1 - α`, comme le `ConformalClassifier` avec trop peu d'échantillons.

Inclusion : `p(c) >= Threshold - 1e-12`, plafonnée à `topK`. Un set vide est une abstention.
La couverture visée est `1 - α` ; elle est **mesurée** par `AdaptiveEvaluator`, pas affirmée.

Rafraîchissement : recalculer le quantile à chaque prédiction coûte un tri de `maxSamples`. On le
met en cache et on le rafraîchit quand `Observe` a ajouté une entrée, tous les `refreshEvery`
appels, ou après `maxThresholdAge` (défaut 5 minutes). Entre deux rafraîchissements, la
décroissance temporelle est ignorée : c'est une approximation bornée par l'intervalle, à nommer
dans la doc.

#### Dérive (P3)

```csharp
public sealed record DriftOptions
{
    public double ExpectedAccuracy { get; init; } = 0.8;
    public double Delta { get; init; } = 0.05;
    public double Threshold { get; init; } = 10;       // en erreurs excédentaires
    public int MinObservations { get; init; } = 50;
    public TimeSpan MinHalfLife { get; init; } = TimeSpan.FromDays(3);
    public TimeSpan MaxHalfLife { get; init; } = TimeSpan.FromDays(365);
    public int StableObservations { get; init; } = 200;
}

public sealed class DriftMonitor
{
    public DriftMonitor(DriftOptions? options = null);

    public void Observe(bool correct, DateTimeOffset at);
    public bool Drifted { get; }
    public int Observations { get; }

    public TimeSpan SuggestedHalfLife(TimeSpan current);
    public void Reset();
}
```

Page–Hinkley sur le flux de correction, sans fenêtre stockée :

```text
m_t   = Σ (ExpectedAccuracy - x_i - Delta)     // x_i = 1 si correct
minM  = min(minM, m_t)
Drifted = (m_t - minM) > Threshold
```

`Observe` exige des `At` non décroissants. `Drifted` reste vrai jusqu'à `Reset()`. Loi de
contrôle, volontairement simple et **suggérée**, pas appliquée d'office :

```text
sur dérive      : τ ← max(τ_min, τ / 2)
sur stabilité   : τ ← min(τ_max, 1.05 · τ)     // après StableObservations
```

`SuggestedHalfLife` est idempotente : après une dérive, elle renvoie la même valeur jusqu'à
`Reset()`, pour éviter de diviser par deux à chaque appel. Le ranker applique via
`Rebase(now, policy)` quand `AutoTuneHalfLife` est vrai ; sinon le client applique lui-même.

Une demi-vie fixe est un aveu : le bon τ dépend du client et du moment. Mais un monitor qui
oscille est pire qu'une demi-vie fixe ; d'où le seuil, le minimum d'observations et la loi 5 %.

#### Évaluation (P2/P3)

```csharp
public sealed record AdaptiveEvaluationOptions
{
    public int TopK { get; init; } = 1;
    public int RecoveryHorizon { get; init; } = 50;
    public int RepetitionHorizon { get; init; } = 50;
    public int BootstrapResamples { get; init; } = 1000;
    public int Seed { get; init; } = 7;
    public double Alpha { get; init; } = 0.10;
    public double MaxImportanceWeight { get; init; } = 10;
}

public static class AdaptiveEvaluator
{
    public static AdaptiveReport Replay<TContext>(
        IEnumerable<FeedbackEvent<TContext>> events,
        IAdaptiveFeedbackPolicy<TContext> policy,
        AdaptiveEvaluationOptions? options = null)
        where TContext : notnull;
}
```

Le contrat est le même que `OnlineClassificationEvaluator` : **prédire d'abord, observer ensuite**,
sinon un événement aide à se classer lui-même. Le rapport contient :

- `LabeledAccuracy`, `AcceptanceRate`, `CorrectionRate`, `AbstentionRate`, `IgnoredRate` ;
- `Ece`, `Brier`, `LogLoss` sur les événements labellisés (acceptation ou correction) ;
- `Recovery` : pour chaque `Corrected`, nombre de prédictions, dans le même contexte, avant que la
  bonne catégorie soit top-1 ; `Recovered`, `Censored`, moyenne, médiane, p90 ;
- `Repetition` : pour chaque `Corrected`, nombre de fois où la même mauvaise proposition est
  réaffichée avant de disparaître ; épisodes non chevauchants par `(contexte, mauvaise catégorie)` ;
- `Coverage` : `NominalCoverage = 1 - α`, `EmpiricalCoverage` = part des labels dans le set,
  `MeanSetSize` ; `NaN` si la politique n'expose pas de set ;
- `Importance` (P3) : IPS auto-normalisé, clipsé à `MaxImportanceWeight`, avec ESS ; `null` si
  aucune propension ;
- `Contexts` : ventilation par clé de contexte (hachée), sans texte ;
- `OrderHash` : SHA-256 de la séquence rejouée (mêmes champs que le log), pour épingler un run.

**Honnêteté sur le label.** `AdaptiveEvaluator` mesure l'accord avec le choix humain journalisé, pas
avec une vérité terrain. Sur un log biaisé (on ne voit que ce qui est accepté ou corrigé), ce n'est
pas de l'accuracy : c'est de l'accuracy **conditionnelle au retour**. Le rapport doit toujours
afficher `LabeledShare` et `AcceptanceRate` à côté, et la doc doit le dire. IPS est une correction,
pas une absolution : il suppose des propensions connues et une ignorabilité qui est souvent fausse
dans un assistant personnel.

## Invariants

1. **Défaut = comportement actuel.** Mémoire vide, `λ`/`γ` quelconques, pas de calibrateur : mêmes
   catégories, même ordre, mêmes probabilités que le modèle texte. Voie rapide bit à bit quand
   aucune renormalisation n'est nécessaire.
2. **Déterminisme.** Même politique + mêmes événements + mêmes `now` ⇒ même modèle, même snapshot,
   même signature. Aucun `DateTime.Now`, aucun `Guid.NewGuid`, aucun hash randomisé
   (`string.GetHashCode` est interdit pour les clés persistées).
3. **Temps injecté.** Chaque méthode qui dépend du temps prend un `DateTimeOffset` ; le fuseau est
   celui de l'appelant. Pas de `TimeProvider` implicite, pas d'horloge locale.
4. **Mémoire bornée.** Caps de contextes, de catégories par contexte et de paires ; purge par âge ;
   pas de liste d'événements non bornée. Le snapshot a une taille majorée par ces caps.
5. **Thread-safety.** Un seul écrivain (`Observe`, `Purge`, `Rebase`, `Prune`), lecteurs concurrents
   (`Prior`, `Vetoed`, `Save`, `Predict`). La décroissance en lecture est calculée, jamais stockée :
   c'est ce qui rend les lecteurs purs. Le ranker prend un verrou lecteur/écrivain qui couvre à la
   fois la mémoire et le calibrateur, pour qu'une prédiction ne voie jamais l'une mis à jour et
   l'autre non.
6. **Snapshot versionné et signé.** `LXFA` v1 : magie, version, signature du modèle (fournie par
   l'appelant), options, politique, mémoire, calibration, puis SHA-256 sur les octets précédents.
   Load refuse une version inconnue, une signature fausse ou un `CodecId` différent.
7. **Vie privée.** Aucun texte dans le snapshot. Clés de contexte hachées par défaut (128 bits,
   SHA-256), codec brut en opt-in explicite. Les labels de catégorie restent en clair : s'ils sont
   sensibles, le client doit entraîner sur des identifiants opaques.
8. **Explicabilité.** Chaque score expose `TextProbability`, `AcceptanceShare`, `RefusalMass`,
   `ContextMultiplier`, `FinalProbability`, `Vetoed`, `InPredictionSet`. `Explain` ajoute le
   `ContextPrior` et, si le modèle le permet, le `ClassificationExplanation` texte.
9. **Pas de boucle de calibration.** Le calibrateur fournit un seuil ; il ne modifie jamais le
   score. `Observe` n'écrit que des entrées ; `PredictSet` ne mute pas (hors cache de seuil).
10. **Veto non destructif.** Un veto retire une proposition, pas la mémoire. Si toutes les
    catégories sont vetoées, on revient à la liste non vetoée et on le signale ; on ne renvoie
    jamais un classement vide à cause d'un veto.
11. **Pas d'introduction de label.** La mémoire repondère les catégories que le modèle propose ;
    elle n'en invente pas. Pour introduire un label, utiliser `BlendContext.Candidates` ou
    réentraîner.
12. **Compatibilité binaire.** Aucun membre ajouté à une interface existante, aucun paramètre
    optionnel ajouté à une signature existante, aucun changement de `LXCL`. Les nouveaux overloads
    et les nouveaux types sont additifs.

## Compatibilité et contraintes du dépôt

- **Cible** : `net10.0`, cœur sans aucune dépendance NuGet. SHA-256, `ReaderWriterLockSlim` et
  `Timeout.InfiniteTimeSpan` sont dans la BCL.
- **CS1591 est une erreur** : chaque membre public ajouté doit avoir son XML doc en anglais, comme
  le reste du dépôt. La spec est en français ; les commentaires livrés seront en anglais.
- **`bench/LexiSharp.ApiDocs -- check`** : tout nouveau type public exige de régénérer `docs/api.md`.
  Le gate vérifie aussi l'unicité des noms simples ; d'où le choix de `AdaptiveFeedbackRanker`
  plutôt que `AdaptiveClassifier`.
- **Version** : 1.3.0 (mineure, additive). `LXCL` reste en version 2 ; le nouveau format est
  `LXFA` v1. Le CHANGELOG dit ce que ça change pour l'appelant, pas l'histoire de l'investigation.
- **Tests** : xUnit, aucun nouveau package. Les tests existent dans `tests/LexiSharp.Tests/` à
  plat ; on suit ce style.
- **Mesure** : toute affirmation de qualité dans la doc ou le CHANGELOG doit pointer vers une
  commande reproductible (`--verify-reference`) ou une section de `docs/`. Pas de chiffre inventé.

## Plan de tests

### P1

`RecencyPolicyTests`
- `Weight` à `t = 0`, `τ`, `2τ` : 1, 0,5, 0,25 ; `MaxAge` ⇒ 0 ; `at > now` ⇒ throw.
- Saturation de fréquence : `occurrences = 1, 8, 64` avec `FrequencySaturation = 7` ⇒ 1, 0,5, 0,1.
- `Weight ∈ [0, 1]` ; `None` ⇒ 1 ; validation des paramètres.
- `Copies` : 1 → 4, 0,5 → 2, 0,25 → 1, expiré → 0 ; mode `Exact` ⇒ throw.

`SampleWeightTests`
- Un modèle linéaire entraîné avec des poids entiers 1/2/3 est identique à un corpus dupliqué
  1/2/3 (mêmes catégories, mêmes prédictions) ; poids 0 ⇒ document ignoré.
- Poids fractionnaire 0,5/1,0 identique à 1/2 copies quand l'arrondi le permet.
- Un poids négatif, NaN ou infini ⇒ `ArgumentException` (le noyau le fait déjà).
- `ClassWeights` et `weightOf` se composent multiplicativement.
- Un modèle entraîné avec poids, sauvegardé en `LXCL`, rechargé, prédit à l'identique sans le
  délégué.
- `SampleWeighting.Expand` : le nombre de copies suit la politique, l'ordre est stable.

### P2

`FeedbackMemoryTests`
- Table des cinq outcomes : chaque effet, y compris `Corrected` qui pénalise `Offered` **et**
  crédite `Accepted`.
- Validation : `Accepted` avec offered ≠ accepted ⇒ throw ; `Rejected` avec accepted ⇒ throw ;
  `Ignored` avec accepted ⇒ throw ; `Abstained` avec offered ⇒ throw ; `At` décroissant ⇒ throw.
- Shrinkage : 1 acceptation dans un contexte ne donne pas `m = 1` ; le global tire vers le haut ;
  `κ` contrôle la vitesse.
- Décroissance : après une demi-vie, la masse est divisée par deux ; après `MaxAge`, zéro.
- Confirmation : `Accepted` diminue `R` de 1, plancher 0 ; `Rejected` l'augmente.
- Veto : 3 refus ⇒ veto ; un refus ancien ne veto plus ; `Ignored` ne veto jamais.
- `Ignored` n'ajoute aucune masse mais augmente `ResponseRate` au dénominateur.
- Caps : `MaxContexts` (LRU), `MaxCategoriesPerContext`, `MaxPairs` ; `Purge` retire les masses
  négligeables.
- `Rebase` : matérialiser puis changer τ donne les mêmes masses qu'une projection directe.
- Lectures concurrentes : `Parallel.For` sur `Prior`/`Vetoed` sans writer ⇒ résultats identiques,
  aucune exception ; un stress writer/lecteurs ne perd aucun événement.
- Codec haché : deux encodages du même contexte sont égaux ; un vecteur SHA-256 connu est stable ;
  le snapshot ne contient pas la chaîne brute. Codec brut : opt-in, `IsOpaque = false`.
- Snapshot : round-trip, signature stable, octets identiques pour un même état ; altération d'un
  octet ⇒ `InvalidDataException` ; version inconnue ⇒ refus ; `CodecId` différent ⇒ refus.

`AdaptiveFeedbackRankerTests`
- Identité : mémoire vide + pas de calibrateur ⇒ résultats du modèle, mêmes probabilités et ordre.
- `λ` et `γ` : une acceptation augmente, un refus diminue, le reste est renormalisé.
- Veto : la catégorie disparaît, les autres se renormalisent ; toutes vetoées ⇒ fallback signalé.
- `Corrected` pénalise bien la mauvaise catégorie dans le contexte, pas globalement.
- `Explain` : les trois contributions sont présentes, `FinalProbability` somme à 1, les vetoes
  sont listés.
- Calibration : set vide ⇒ `Abstained` ; `MinScore` ⇒ `Abstained` sans calibrateur.
- `Observe` sans `Offered` incrémente `MissingOfferedScores` et n'altère pas le calibrateur.
- `limit` : `Results` est le top `limit`, `AllScores` reste complet ; le set est un préfixe.
- Tie-break : deux scores égaux gardent l'ordre du modèle.

`WeightedConformalTests`
- Poids tous à 1 ⇒ quantile identique à `ConformalClassifier` (même formule, mêmes scores).
- Couverture empirique sur un flux stationnaire synthétique (n = 5 000, α = 0,1) dans ±2 points.
- Après dérive, la repondération change le seuil et la couverture reste proche du nominal.
- `minSamples` non atteint ⇒ fallback `1 - α` ; tout expiré ⇒ fallback.
- `Purge`, `Reset`, round-trip du snapshot, entrées `maxSamples` bornées.
- `refreshEvery` / `maxThresholdAge` : le seuil change au plus à l'intervalle prévu ; déterminisme
  pour une même séquence de `now`.

`AdaptiveEvaluatorTests`
- Flux à la main : 1 accepté, 1 corrigé, 1 rejeté, 1 ignoré, 1 abstention ; vérifier chaque
  compteur, `LabeledAccuracy`, `AcceptanceRate`, `ResponseRate`.
- Temps de récupération exact sur un flux construit ; censure au-delà de l'horizon.
- Regret de répétition exact ; épisodes non chevauchants ; remise à zéro quand la proposition change.
- ECE/Brier/log-loss sur des probabilités connues ; label absent ⇒ score de non-conformité 1.
- IPS : propensions connues, valeur calculée à la main ; clip ; ESS ; propension manquante ⇒
  `Importance = null` ; `Abstained`/`Ignored` exclus.
- `OrderHash` stable ; bootstrap déterministe avec la graine.

`AdaptiveSnapshotTests`
- `LXFA` v1 : round-trip mémoire + calibration + options + signature modèle.
- Signature altérée, version inconnue, `CodecId` différent, policy invalide ⇒ refus.
- Ordre canonique : deux dictionnaires insérés dans un ordre différent produisent les mêmes octets.

### P3

`DriftMonitorTests`
- Flux stationnaire ⇒ pas de dérive après `MinObservations`.
- Marche de 1 vers 0 ⇒ détection dans une fenêtre bornée ; `Reset` repart.
- `SuggestedHalfLife` : dérive ⇒ τ/2, plancher ; stabilité ⇒ ×1,05, plafond ; idempotence.
- `At` décroissant ⇒ throw.

`AdaptiveDriftBenchmarkTests`
- Générateur déterministe (même graine ⇒ même flux, même `OrderHash`).
- Le pin `--verify-reference` reproduit les chiffres à la tolérance près.

## Benchmark de dérive

Le benchmark ne prouve pas un gain en production ; il isole le mécanisme. Il est **synthétique**,
donc reproductible, rapide, et sans téléchargement.

### Générateur

- `K = 8` catégories, vocabulaire synthétique par catégorie, longueur 8–20 tokens.
- Un « concept » est une table token → catégorie. À mi-parcours, une fraction des tokens change de
  catégorie (label swap) ou le vocabulaire tourne (vocabulary rotation).
- Flux de 5 000 événements, 20 graines fixes.
- Utilisateur simulé : accepte avec une probabilité qui décroît avec le rang ; sinon corrige avec
  une probabilité donnée, sinon refuse ; ignore une fraction fixe. **Tous** les événements sont
  journalisés, y compris `Ignored` et `Abstained` — c'est ce qui permet de mesurer le biais.
- La politique ne voit que les retours ; le harnais connaît le label caché et score contre lui.

### Variantes

| Variante | Ce qu'elle active |
|---|---|
| `static` | poids 1, pas de mémoire, pas de calibration |
| `recency` | P1 : `RecencyPolicy` + `weightOf` |
| `memory` | P2 : `FeedbackMemory` + `AdaptiveFeedbackRanker` |
| `memory+drift` | P3 : `DriftMonitor` + demi-vie adaptative |

### Métriques

Fenêtre post-dérive : accuracy@1 (vraie), temps de récupération, regret de répétition, ECE,
couverture réelle vs nominale, `ResponseRate`. Intervalle bootstrap apparié sur les 20 graines.

### Hypothèses pré-enregistrées

- **H1** : `memory+drift` bat `static` d'au moins 5 points d'accuracy@1 post-dérive, IC 95 %
  apparié excluant 0, sur au moins 15/20 graines.
- **H2** : le temps de récupération médian est au plus la moitié de celui de `static`.
- **H3** : le regret de répétition moyen est ≤ 1,0 après la première correction.
- **H4** : sur un flux stationnaire, la couverture empirique est à ±3 points du nominal (α = 0,1,
  n = 5 000).
- **H5** : mémoire vide ⇒ identité exacte avec le modèle de base (test unitaire, pas benchmark).

Si H1 échoue, l'affirmation « la décroissance bat le statique » est fausse : on le rapporte et on
ne tune pas le générateur après coup. C'est la règle d'ADR 0005.

### Intégration

- Verbe `classification-drift` dans `bench/LexiSharp.Eval`, avec `--seed`, `--events`,
  `--drift-at`, `--drift-kind`, `--feedback-rate`.
- Section `adaptiveDriftPins` dans `bench/LexiSharp.Eval/reference/pinned.json`, vérifiée par
  `--verify-reference` ; schéma additif, version 1 acceptée sans pins adaptatifs.
- Table et commande dans `docs/benchmarks.md`, avec la phrase qui dit que c'est synthétique.
- Les chiffres sont ajoutés dans la PR qui les mesure ; la spec ne les invente pas.

## Découpage et livrables

| Phase | Contenu | Ce que ça débloque | PR |
|---|---|---|---|
| **P1** | `RecencyPolicy`, `SampleWeightMode`, `TimedDocument`, `SampleWeighting`, overloads `weightOf` sur les trois façades texte | Poids exacts, plus de copies ; adoptable seul | 1 |
| **P2a** | `FeedbackMemory`, `ContextPrior`, codecs, options, snapshot, tests | Le cœur « mémoire par contexte », utilisable sans la façade | 1 |
| **P2b** | `AdaptiveFeedbackRanker`, `AdaptiveRanking`, `AdaptiveExplanation`, `WeightedConformalCalibrator`, `AdaptiveEvaluator` (métriques de base) | La couche complète, multi-clients | 1–2 |
| **P3a** | `DriftMonitor`, `Rebase`, IPS/ESS | La demi-vie adaptative, l'évaluation de logs biaisés | 1 |
| **P3b** | Générateur de dérive, verbe CLI, pins, docs, benchmark | La preuve mesurée | 1 |

Chaque phase est livrable indépendamment. P2 ne dépend pas de P3 ; P3 est optionnel pour un client
qui a déjà une demi-vie fixe.

### Definition of done par phase

- **P1** : API + tests + doc `docs/text-analysis.md` (section « Recency and sample weights ») +
  `CHANGELOG.md` 1.3.0 + `docs/api.md` régénéré. Aucun comportement existant ne change.
- **P2** : API + tests + `docs/adaptive-feedback.md` (anglais, page publique) + `CHANGELOG.md` +
  `docs/api.md` + snapshot `LXFA` v1 + test d'identité.
- **P3** : `DriftMonitor` + IPS + `classification-drift` + pin `pinned.json` + table
  `docs/benchmarks.md` + note de migration du client de référence.

## Test de vérité : ce que le client de référence supprime

Si la spec est bonne, un client qui implémentait ces mécanismes à la main supprime :

| Mécanisme côté client | Remplacé par | Phase |
|---|---|---|
| calcul de fraîcheur maison | `RecencyPolicy` + `weightOf` | P1 |
| quantification en 1–4 copies | `SampleWeightMode.Exact` (repli `Quantized`) | P1 |
| calcul des poids dans la boucle d'entraînement | `RecencyPolicy.Weight` + closure `(document, at)` | P1 |
| liste de refus maison | `FeedbackMemory<string>` | P2 |
| post-traitement des refus | `AdaptiveFeedbackRanker` (multiplicateur + veto) | P2 |
| poids recalculés dans la signature du modèle | `AdaptiveSnapshot.ModelSignature` + `Signature` | P2 |
| tests manuels de répétition | `AdaptiveEvaluator.Repetition` | P2 |
| conformal à fenêtre fixe de 90 jours | `WeightedConformalCalibrator` | P2 |
| demi-vie fixe | `DriftMonitor` | P3 |

Le client garde :

- la construction des `FeedbackEvent` (le domaine : familles, identifiants, règles métier) ;
- la consommation d'`AdaptiveRanking` (mapping vers l'UI, planchers métier) ;
- le corpus, les stopwords, les poids de fusion et les règles qui ne sont pas des mathématiques
  génériques.

**Critère d'échec** : si une suppression exige un hook spécifique au client dans LexiSharp
(par exemple « le refus divise par deux sauf pour la famille X »), la spec est trop étroite. Il faut
ajouter un seam générique, pas une branche du domaine dans la bibliothèque.

## Pièges nommés

1. **Biais de feedback.** On n'apprend que de ce qui est accepté. Si les refus et les abstentions ne
   sont pas journalisés, la calibration est conditionnelle à l'acceptation. `Ignored` n'est **pas**
   un refus : il ne doit jamais augmenter `R`. Le rapport expose `ResponseRate` et `LabeledShare`.
2. **Exploration.** Une politique top-k déterministe se fige : elle ne propose jamais l'alternative
   qui aurait été meilleure. ε-greedy / Thompson est une option P3 pour les clients « ranking »,
   pas pour un client où l'utilisateur voit tous les retours. Sans exploration, IPS n'a pas de
   support.
3. **Explosion des contextes.** Un contexte par fichier est trop. Le codec accepte une clé
   canonique hiérarchique (`compte/dépôt/dossier`) ou un hash ; les caps bornent la mémoire. Le
   shrinkage est à deux niveaux (contexte → global) ; le hiérarchique à trois niveaux est une
   extension P3, pas un besoin P2.
4. **Fuite de texte.** La mémoire ne stocke jamais le texte ; le snapshot ne contient que des clés
   hachées et des labels. Le log, lui, contient le texte : c'est le client qui décide de sa rétention.
5. **Labels sensibles.** Les catégories sont en clair dans le snapshot. Si elles sont sensibles,
   entraîner sur des identifiants opaques ; la spec ne peut pas hacher des labels dont elle a besoin
   pour repondérer.
6. **Horloge et ordre.** Pas de `DateTime.Now` ; pas d'événement futur ; `At` non décroissant par
   mémoire. Un flux multi-appareils doit être ordonné par une horloge autoritaire avant `Observe`.
7. **Changement de modèle.** Un swap de `AdaptiveClassifier` invalide les scores de calibration
   stockés (ils venaient de l'ancien modèle). Appeler `calibrator.Reset()` après un swap, ou
   reconstruire le calibrateur ; sinon la couverture ment.
8. **Famine de veto.** Si toutes les catégories sont vetoées, on revient à la liste non vetoée. Un
   taux élevé de `VetoFallback` signale un `VetoAfter` trop bas ou une mémoire trop agressive.
9. **Double comptage.** Mettre à jour le modèle texte **et** la mémoire sur le même événement
   double le signal. Le ranker ne touche pas au modèle texte ; c'est le client qui choisit son
   chemin `Learn`/`Reinforce`/retrain.
10. **Coût du conformal.** Repondérer et trier à chaque prédiction est trop cher ; le cache de seuil
    est une approximation bornée, pas une exactitude. Le benchmark mesure le coût.
11. **Dérive du texte.** `DriftMonitor` détecte une baisse de correction ; il ne répare pas un
    vocabulaire qui a changé. La demi-vie adaptative aide la mémoire, pas les coefficients ; un
    réentraînement reste nécessaire.
12. **Collision de hash.** 128 bits pour ≤ 100 000 contextes : probabilité négligeable, mais à
    documenter. Un client qui craint la collision peut fournir un codec avec un `Id` et un hash
    plus long.

## Alternatives rejetées

- **Un plus gros modèle (embeddings, LLM).** Ne répond pas à « mémoriser un refus par contexte »,
  coûte cher, n'est pas explicable, et sort de la philosophie zéro dépendance.
- **Poids globaux seulement.** Un contexte par famille dérive à un rythme différent ; une
  demi-vie unique moyenne les régimes et rate la dérive locale.
- **Un modèle par contexte.** Affamé en données, explosion du nombre de modèles, impossible à
  calibrer. Le shrinkage deux niveaux est le compromis.
- **Copies de documents comme défaut.** Gonfle les comptes, interagit avec les priors et
  `Unlearn` ; c'est un repli pour les modèles sans poids, pas la voie principale.
- **Liste de refus sans décroissance.** Punition permanente, aucune récupération après un
  changement d'habitude. Le veto décroît.
- **Conformal non pondéré.** La couverture moyenne sur 90 jours masque la dérive ; c'est le défaut
  actuel, nommé dans le problème.
- **Bayésien hiérarchique complet.** Plus puissant, mais coûteux, moins explicable, et difficile à
  sérialiser sans dépendance. Le shrinkage ponctuel couvre le besoin.
- **ADWIN.** Stocke une fenêtre variable, coûteux en mémoire et en déterminisme. Page–Hinkley O(1)
  suffit pour un flux de corrections.
- **Stocker les événements bruts.** Non borné, et fait fuiter le texte. Les agrégats décroissants
  suffisent.
- **Ajouter un timestamp à `SearchDocument`.** Change un record central, son égalité et sa
  sérialisation, pour un besoin qui est une closure côté client. `weightOf` + `(document, at)` est
  plus petit et ne touche pas le retrieval.
- **Ajouter des membres à `ITextClassifier`.** Casserait toutes les implémentations. La façade
  séparée est additive.
- **Nommer la façade `AdaptiveClassifier<TContext>`.** Collision avec le wrapper de swap existant.
- **Thompson sampling en P2.** Hors scope, et inutile pour un client qui voit tous les retours.

## Questions ouvertes

1. **Contexte hiérarchique** : faut-il un shrinkage à trois niveaux (dossier → dépôt → global) en
   P2, ou le codec canonique `"dépôt/dossier"` suffit-il ? La spec parie sur le second ; à valider
   sur le client de référence.
2. **Poids par correction** : `Learn`/`Reinforce` n'ont pas de poids par événement (seulement
   `CorrectionWeight` global). Faut-il un `Learn(document, weight)` en P3, ou la mémoire
   contextuelle suffit-elle ?
3. **Calibration post-veto ou pré-veto** : la spec calibre les scores **post-veto** (ce que
   l'utilisateur voit). Si un client préfère la distribution texte brute, il faut un second
   calibrateur ; à mesurer.
4. **IPS avec récompense partielle** : `Ignored` est-il un 0 ou une observation manquante ? La spec
   le traite comme manquant (exclu). Un client qui suppose « ignorer = ne pas aimer » obtient une
   autre politique ; il doit le déclarer.
5. **DR** : hors scope sans modèle de récompense. Un `IRewardModel` optionnel en P3 ?
6. **Taux de réponse** : faut-il ignorer les contextes à faible `ResponseRate` dans le shrinkage ?
   La spec l'expose mais ne l'utilise pas ; à mesurer.
7. **Snapshot mémoire seul** : `FeedbackMemory.Save/Load` est en mémoire ; faut-il un fichier
   `LXFB` séparé, ou `LXFA` (avec modèle + calibration) est-il le seul format ? La spec parie sur
   `LXFA` seul.

## Différences avec la proposition initiale

1. **`AdaptiveClassifier<TContext>` → `AdaptiveFeedbackRanker<TContext>`** : le nom est déjà pris
   par le wrapper de swap atomique. Les deux restent, à des niveaux différents.
2. **`Weight` est dans `[0, 1]`, pas `(0, 1]`** : `MaxAge` doit pouvoir jeter un échantillon.
3. **`weightOf` n'est pas sérialisé** : un délégué n'est pas une donnée, comme le tokenizer. La
   politique, elle, est un record sérialisable dans le snapshot ; les coefficients portent déjà
   l'effet des poids.
4. **Overloads, pas de paramètre optionnel ajouté** : ajouter un paramètre optionnel à un
   constructeur existant casse la compatibilité binaire. La spec ajoute des constructeurs à trois
   paramètres.
5. **`Save()` → `Save(now)`** : une projection décroissante a besoin d'un instant. `Save` ne mute
   pas l'état vivant.
6. **`Purge(before)` → `Purge(now)`** : l'expiration est relative à l'instant courant, pas un
   seuil absolu ambigu.
7. **`FeedbackSnapshot<TContext>` → snapshot non générique + codec** : les clés persistées sont des
   chaînes opaques (hachées par défaut). `Load` n'a pas besoin de décoder le contexte ; il hache à
   la volée pour interroger.
8. **`Prior` ne renvoie pas une distribution** : il renvoie de l'évidence par catégorie (masse,
   part rétrécie, refus). Le ranker normalise après multiplication.
9. **`Corrected` compte aussi comme un refus** pour la catégorie proposée, pas seulement comme une
   acceptation pour la nouvelle. Sinon une correction répétée ne décourage jamais la mauvaise
   proposition.
10. **`Ignored` n'est jamais un refus** : il compte comme exposition, pas comme masse négative.
11. **Conformal pondéré qui coïncide avec l'existant** quand les poids valent 1 : la formule
    `(1-α)(W+1)` redonne exactement `ceil((n+1)(1-α))-1`.
12. **`DriftMonitor` suggère, n'applique pas** : `Rebase` est explicite, avec une loi 5 % et une
    hystérésis. Une demi-vie qui change à chaque prédiction oscille.
13. **IPS auto-normalisé et clipsé, DR hors scope** sans modèle de récompense. IPS est un
    diagnostic, pas un feu vert de déploiement.
14. **Pas de mise à jour du modèle texte dans `Observe`** : la mémoire et la calibration seulement.
    Le texte reste le chemin `Learn`/`Reinforce`/retrain du client.
15. **Conformal désactivé par défaut** : le défaut est le modèle nu ; l'abstention est opt-in.

## Références

- `docs/evaluation.md` — la règle « mesurer avant de changer un poids ».
- `src/LexiSharp/Classification/LinearClassifier.cs`, `DecisionTreeClassifier.cs`,
  `RandomForestClassifier.cs` — `weightOf` génériques, façades texte à `weightOf: null`.
- `src/LexiSharp/Classification/ConformalClassifier.cs` — split-conformal non pondéré, formule du
  quantile et fallback.
- `src/LexiSharp/Classification/OnlineClassificationEvaluator.cs` — prédire-puis-apprendre,
  bootstrap par blocs, `OrderHash`.
- `src/LexiSharp/Classification/ModelPersistence.cs` — format `LXCL` versionné, « a delegate is not
  data », hash d'espace vectoriel.
- `src/LexiSharp/Classification/AdaptiveClassifier.cs` — swap atomique, `TrySwapSignificant`.
- `src/LexiSharp/Classification/TemporalPrior.cs` — prior temporel existant, horloge injectée.
- `src/LexiSharp/Core/IReinforceableTextClassifier.cs` — ledger global, sans contexte ni temps.
- `src/LexiSharp/Core/ITextPriorSource.cs`, `ICandidateTextSource.cs` — évidence additive en log,
  contrat de non-introduction de label.
- Page–Hinkley (1971) ; ADWIN, Bifet & Gavaldà (2007) — détection de dérive.
- Barber, Candès, Ramdas, Tibshirani, *Conformal prediction beyond exchangeability* (2023) —
  quantile conformal pondéré.
- Dudík, Langford, Li (2011) ; Strehl et al. (2010) — IPS/DR pour l'évaluation hors ligne.

## Amendement — 2026-11-26

La proposition a été complétée par `doc/adr/0007-decision-preference.md`. Le présent
document reste la **phase 1** : poids d'échantillon, politique de fraîcheur, mémoire contextuelle et
calibration pondérée. La phase 2 (décision au coût) et la phase 3 (séquence, préférence, évaluation
contrefactuelle) sont dans l'ADR 0007.

Deux conséquences de cet amendement :

- Le `DriftMonitor` et le banc `adaptive-cost` passent dans la phase 3 de l'ADR 0007 :
  leur preuve est le coût de revue, pas seulement l'accuracy. L'API et les invariants de la
  mémoire ne changent pas.
- Le format `LXFA`, les noms `FeedbackMemory`/`AdaptiveFeedbackRanker` et les invariants de
  compatibilité binaire restent tels quels. L'ADR 0007 ajoute des types, il n'en modifie pas.
