# ADR 0007 — Décider au coût et apprendre des préférences

- **Statut** : proposé
- **Date** : 2026-11-26
- **Dépôt** : LexiSharp
- **Série** : complète `doc/adr/0006-adaptive-feedback.md`, qui reste la phase 1.
- **Portée** : LexiSharp 1.4 (décision) et 1.5 (préférences, évaluation contrefactuelle).

> Ce fichier vit sous `doc/`, pas sous `docs/` : il n'est pas publié par GitHub Pages. Il est écrit
> pour être collé dans une issue de design, ou déplacé sous `docs/` si la série ADR devient publique.

## Contexte

L'ADR 0006 apprend d'un flux de retours : poids temporels, mémoire par contexte, calibration
pondérée. Il reste deux manques, tous les deux dans le périmètre « search + measurement » de
LexiSharp :

- **Une distribution calibrée ne dit pas quoi faire.** `MinProbability` et
  `ConformalClassifier` savent déjà s'abstenir, mais il n'existe pas de décision coût-sensible :
  accepter, demander, s'abstenir ou explorer, en minimisant un coût d'attention.
- **Les choix relatifs ne sont pas appris.** Quand l'utilisateur choisit le 3ᵉ candidat, c'est une
  préférence ; quand il refuse, c'est un négatif. `OnlineClassificationEvaluator` mesure un flux
  labellisé, mais ne consume pas les rangs journalisés et ne corrige pas le biais de la politique
  qui a produit le log.

## Frontière

**LexiSharp fournit des distributions calibrées et les outils pour les mesurer et décider. Le
client possède la structure temporelle, les contraintes métier et l'interface.**

Cette ligne est volontaire. La bibliothèque ne découpe pas un flux temporel, ne détecte pas de
ruptures, n'impose pas de durées et ne connaît pas le domaine. Elle prend une distribution
calibrée, des coûts, des événements horodatés et des préférences ; elle rend une action, une
mesure ou un re-rank. Le client garde l'unité de travail, les règles et l'écran.

## Non-objectifs

- **Pas de structure temporelle.** Pas de découpage d'un flux, pas de rupture, pas de durée : c'est
  le client, au-dessus des distributions que LexiSharp fournit.
- **Pas un nouveau classifieur, pas de LLM, pas d'UI, pas de sémantique métier.** La couche prend
  les distributions et les features du client.
- **Pas de matrice de coûts dense imposée.** Le cas courant est un coût par catégorie réelle ; une
  matrice complète est une implémentation de l'interface.
- **Pas un bandit contextuel en P3.** L'apprenant de préférence est un re-ranker linéaire pairwise ;
  l'exploration est une option P4.
- **Pas de DR sans modèle de récompense.** IPS suffit pour un diagnostic ; DR exige `q(x,a)`, que
  le log ne fournit pas.

## Décision

Deux briques, livrables séparément :

- **P2 — `DecisionLayer`** : `IDecisionPolicy`, `DecisionCosts`, `CostAwareDecisionPolicy`. Petit,
  testable, adoptable seul.
- **P3 — `PreferenceLearner` + `PreferenceEvaluator`** : un re-ranker pairwise et une évaluation
  IPS/ESS. C'est le chaînon entre `QueryFeedbackHistory` (une table requête → document) et un
  `IReranker` appris qui généralise à des requêtes jamais vues.

Composition :

```text
distribution calibrée (0006)
  → DecisionLayer : Accept / Ask / Abstain / Explore
  → retour : label, préférence, contrainte
  → FeedbackMemory + PreferenceLearner
```

## API

### DecisionLayer

```csharp
namespace LexiSharp.Adaptive;

public interface IMisclassificationCost
{
    double Cost(string actual, string predicted);
}

public sealed class UniformMisclassificationCost : IMisclassificationCost
{
    public static UniformMisclassificationCost One { get; }
    public UniformMisclassificationCost(double cost);
}

public sealed class CategoryMisclassificationCost : IMisclassificationCost
{
    public CategoryMisclassificationCost(
        IReadOnlyDictionary<string, double> costs,
        double fallback = 1.0);
}

public sealed record DecisionCosts
{
    public double AskCost { get; init; } = 1.0;
    public double AbstainCost { get; init; } = 2.0;
    public double ExploreCost { get; init; } = 3.0;
    public double ResidualCostAfterAsk { get; init; }
    public IMisclassificationCost Misclassification { get; init; } = UniformMisclassificationCost.One;
    public double? ValueOfInformationThreshold { get; init; }
}

public enum DecisionKind { Accept, Ask, Abstain, Explore }

public sealed record DecisionOption(
    DecisionKind Kind,
    string? Category,
    IReadOnlyList<string> Candidates,
    double ExpectedCost,
    double? ValueOfInformation);

public sealed record Decision(
    DecisionKind Kind,
    string? Category,
    IReadOnlyList<string> Candidates,
    double ExpectedCost,
    double? ValueOfInformation,
    IReadOnlyList<DecisionOption> Options);

public interface IExplorationPolicy
{
    string? Choose(IReadOnlyList<ClassificationResult> calibrated, DecisionCosts costs);
}

public sealed record DecisionOptions
{
    public double AskCandidateMass { get; init; } = 0.9;
    public int MinAskCandidates { get; init; } = 2;
    public int MaxAskCandidates { get; init; } = 3;
    public bool AllowExplore { get; init; }
    public IExplorationPolicy? Exploration { get; init; }
}

public interface IDecisionPolicy
{
    Decision Decide(IReadOnlyList<ClassificationResult> calibrated, DecisionCosts costs);
}

public sealed class CostAwareDecisionPolicy : IDecisionPolicy
{
    public CostAwareDecisionPolicy(DecisionOptions? options = null);
    public Decision Decide(IReadOnlyList<ClassificationResult> calibrated, DecisionCosts costs);
}
```

`Decision` est une **structure**, pas une phrase. `Ask` porte les candidats à désambiguïser ; l'UI
formule la question. La bibliothèque ne génère pas de texte libre et ne connaît pas le domaine.

#### Coûts

```text
accept(c)  = Σ_{y != c} p(y) · Cost(y, c)
ask        = AskCost + ResidualCostAfterAsk
abstain    = AbstainCost
explore(c) = ExploreCost
```

- `ask` suppose une réponse **oracle** ; `ResidualCostAfterAsk` modélise une réponse imparfaite.
  Sans ce terme, la couche sur-demande.
- `ask` n'est considéré que si le set de candidats contient au moins `MinAskCandidates` catégories.
  Le set est le plus petit préfixe (probabilité décroissante) dont la masse atteint
  `AskCandidateMass`, borné par `MaxAskCandidates`.
- `explore` n'est considéré que si `AllowExplore` et `Exploration != null`. La politique
  d'exploration choisit la catégorie **et** sa valeur ; `ExploreCost` est le coût attendu de cette
  action, pas `ExploreCost + accept(c)` — sinon l'exploration serait toujours dominée.
- Une action peut être désactivée avec `double.PositiveInfinity`.
- `VOI(ask) = min(accept(best), abstain) - ask` ; `ValueOfInformationThreshold` bloque `Ask` si le
  VOI est insuffisant, mais la valeur reste exposée.
- Choix : coût minimal ; égalité ⇒ `Accept < Ask < Abstain < Explore`, puis catégorie ordinale.
- Distribution vide ⇒ `Abstain` si `AbstainCost` est fini, sinon `ArgumentException`. Distribution
  non normalisée, probabilité hors `[0, 1]`, catégorie dupliquée ou coût négatif ⇒ exception.

Le calibrage est une **précondition** : le client branche `CalibratedClassifier` ou le
`WeightedConformalCalibrator` de 0006. La couche ne peut pas vérifier qu'une distribution est
calibrée.

### PreferenceLearner

```csharp
public sealed record PreferenceEvent(
    string Text,
    string ContextKey,
    IReadOnlyList<ClassificationResult> Candidates,
    string Chosen,
    DateTimeOffset At,
    double? Propensity = null);

public sealed record PreferenceItem(
    string Text,
    string ContextKey,
    string Category);

public sealed record PreferenceOptions
{
    public int Epochs { get; init; } = 10;
    public double LearningRate { get; init; } = 0.1;
    public double L2 { get; init; } = 1e-4;
    public int Seed { get; init; } = 7;
    public int MaxFeatures { get; init; } = 50_000;
    public int MaxEvents { get; init; } = 100_000;
    public bool HashFeatures { get; init; }
    public int HashDimension { get; init; } = 1 << 16;
}

public interface IPreferencePolicy
{
    IReadOnlyList<ClassificationResult> ReRank(
        string text,
        string contextKey,
        IReadOnlyList<ClassificationResult> candidates);
}

public sealed class TextPreferenceFeatureSource : IFeatureSource<PreferenceItem>;

public sealed class PreferenceLearner : IPreferencePolicy
{
    public PreferenceLearner(
        IFeatureSource<PreferenceItem>? features = null,
        PreferenceOptions? options = null);

    public void Train(IEnumerable<PreferenceEvent> events);
    public void Observe(PreferenceEvent preference);
    public IReadOnlyList<ClassificationResult> ReRank(
        string text,
        string contextKey,
        IReadOnlyList<ClassificationResult> candidates);

    public PreferenceSnapshot Save(DateTimeOffset now);
    public static PreferenceLearner Load(
        PreferenceSnapshot snapshot,
        IFeatureSource<PreferenceItem>? features = null);
}
```

L'apprenant est un **re-ranker linéaire pairwise**, pas un bandit :

- un vecteur de poids sparse par catégorie sur `φ(text, ContextKey)` ; la source par défaut
  `TextPreferenceFeatureSource` émet des unigrammes de texte et un feature `ctx:<clé>` ;
- pour chaque événement, `Chosen` est comparé à chaque autre candidat ; perte logistique pairwise
  (`RankNet`), AdaGrad, L2, epochs et shuffle à graine fixe ;
- `ReRank` calcule `log p(c) + w_c · φ`, puis normalise ; aucune catégorie n'est introduite ;
  sans poids, l'ordre et les probabilités d'origine sont conservés ;
- `Train` est la référence déterministe ; `Observe` est la commodité en ligne ;
- `Candidates` doit être normalisée, sans doublon, et contenir `Chosen` ; `Propensity`, si présent,
  est dans `(0, 1]` ;
- **snapshot** `PreferenceSnapshot` versionné, signé, avec le vocabulaire (mode vocabulaire) ou les
  indices (mode haché). La source de features n'est pas dans le fichier, comme le tokenizer de
  `LXCL` : charger avec la même configuration.

### PreferenceEvaluator

```csharp
public sealed record PreferenceEvaluationOptions
{
    public int Seed { get; init; } = 7;
    public double MaxImportanceWeight { get; init; } = 10;
}

public sealed record RankSlice(
    int LoggedRank,
    int Events,
    double Top1Agreement,
    double MeanReciprocalRank);

public sealed record PreferenceReport(
    int Events,
    int WithPropensity,
    double Top1Agreement,
    double MeanReciprocalRank,
    double? IpsTop1,
    double EffectiveSampleSize,
    int Clipped,
    IReadOnlyList<RankSlice> ByLoggedRank,
    string OrderHash);

public static class PreferenceEvaluator
{
    public static PreferenceReport Replay(
        IEnumerable<PreferenceEvent> events,
        IPreferencePolicy policy,
        PreferenceEvaluationOptions? options = null);
}
```

- `Top1Agreement` : part où le top-1 re-ranké est `Chosen`.
- `MRR` : moyenne de `1 / rang(Chosen)`.
- `ByLoggedRank` : ventilation par rang de `Chosen` dans le log ; c'est la mesure directe du
  « 3ᵉ candidat ».
- `IpsTop1` : auto-normalisé et clipsé, sur les événements qui portent une propension :

```text
w_i = 1 / Propensity_i
m_i = 1{top1_policy(x_i) == Chosen_i}
IpsTop1 = Σ (m_i · w_i) / Σ w_i
ESS = (Σ w_i)² / Σ w_i²
```

Il estime la probabilité que la politique cible choisisse l'action journalisée, sous la
distribution de log. Hypothèses : propensions connues, positivité, ignorabilité. Ce n'est pas une
mesure de satisfaction. `Replay` évalue une politique **fixe** ; pour un apprenant en ligne, faire
un warmup, puis `ReRank` avant `Observe` à chaque événement, sans entraîner sur le flux évalué.

## Invariants

1. **Défaut = comportement actuel.** Sans `DecisionLayer`, le client garde son seuil. Sans
   `PreferenceLearner`, `ReRank` est l'identité (mêmes catégories, même ordre, mêmes probabilités).
2. **Déterminisme.** Décision à ordre d'action fixe ; apprenant à graine fixe et ordre fixe ;
   aucun `DateTime.Now`, aucun aléa non semé.
3. **Temps injecté.** `PreferenceEvent.At` est fourni par l'appelant ; `DecisionLayer` est pur.
4. **Coûts explicites.** Finis ou `+∞`, non négatifs, dans l'unité du client. La couche minimise le
   coût attendu, pas l'accuracy.
5. **Calibration préalable.** La décision et le re-rank supposent une distribution calibrée ; sinon
   ils optimisent une probabilité fausse.
6. **Pas de label inventé.** Les couches choisissent ou re-rankent parmi les catégories existantes.
7. **Borne mémoire.** `MaxFeatures`/`MaxEvents` pour l'apprenant ; `DecisionLayer` est O(K).
8. **IPS honnête.** Pas d'IPS sans propension ; `ESS` et `Clipped` sont rapportés ; DR est absent
   sans modèle de récompense.
9. **Additif.** Aucun membre ajouté à `ITextClassifier`, aucun changement de `LXCL`/`LXFA`. Les
   nouveaux types sont dans `LexiSharp.Adaptive`.

## Compatibilité et contraintes du dépôt

- **Cible** : `net10.0`, cœur sans dépendance NuGet.
- **CS1591 est une erreur** : XML docs en anglais.
- **`bench/LexiSharp.ApiDocs -- check`** : `docs/api.md` régénéré ; noms simples uniques.
  `DecisionExplanation` est déjà pris par l'arbre de décision — ne pas le réutiliser.
- **Versions** : P2 en 1.4.0, P3 en 1.5.0. `LXFA` reste le snapshot adaptatif de 0006 ;
  `PreferenceSnapshot` est le format de l'apprenant.
- **Mesure** : le banc `adaptive-cost` est la preuve ; aucun chiffre dans la doc sans commande
  reproductible.

## Plan de tests

### DecisionLayer

- Coûts uniformes : accepte à haute confiance, s'abstient à basse confiance.
- Coût par catégorie : une catégorie chère à manquer fait préférer `Ask`.
- `Ask` : plus petit préfixe à `AskCandidateMass`, borné ; moins de deux candidats ⇒ pas d'`Ask`.
- VOI : `ask` choisi si `ask < min(accept, abstain)` ; seuil bloque.
- `ResidualCostAfterAsk` élevé ⇒ `Abstain`.
- Actions infinies désactivées ; `Explore` seulement avec politique explicite.
- Matrice asymétrique via `IMisclassificationCost`.
- Distribution vide, non normalisée, négative, dupliquée ⇒ exception.
- Tie-break déterministe ; `Options` contient les quatre actions.

### PreferenceLearner

- Sans entraînement, `ReRank` est l'identité exacte.
- Une préférence `B > A` augmente le score de `B` et diminue celui de `A`.
- `Chosen` absent, doublons, distribution non normalisée ⇒ exception.
- `Train` déterministe avec la même graine ; `Observe` met à jour en ligne.
- Cap de features ; mode haché sans vocabulaire.
- `ReRank` normalise, conserve le set, n'introduit aucune catégorie.
- Snapshot : round-trip, signature, version inconnue ⇒ refus.
- `PreferenceEvaluator` : top-1, MRR, `ByLoggedRank`, IPS/ESS, propension manquante ⇒ null.

## Benchmark

Le banc `adaptive-cost` réutilise le générateur synthétique de 0006 et ajoute des coûts et des
préférences. Il reste synthétique : il isole le mécanisme, il ne prouve pas un gain en production.

Variantes : `static` (seuil fixe), `adaptive` (0006), `decision` (P2), `+preference` (P3).

Métriques : `CostPer100`, `ReviewUnitsPerCorrectLabel`, `AcceptedPrecision`, `AskRate`,
`AbstainRate`, `Top1Agreement`, `MRR`, `IpsTop1`, `ESS`, et les métriques de 0006.

Hypothèses pré-enregistrées :

- **D1** : `decision` réduit `CostPer100` d'au moins 20 % contre `static` à précision acceptée
  égale, IC 95 % apparié excluant 0, sur au moins 15/20 graines.
- **D2** : `AskRate` ≤ 25 % sur le flux de référence, avec une précision acceptée ≥ 95 %.
- **P1** : `+preference` améliore `Top1Agreement` d'au moins 5 points contre `decision`, avec l'IC
  IPS excluant 0.
- **P2** : identité par défaut (tests unitaires).

Si D1 échoue, la couche ne mérite pas d'entrer dans la bibliothèque : on le rapporte, on ne tune
pas le générateur après coup. C'est la règle d'ADR 0005.

## Découpage

| Phase | Contenu | Débloque | Version |
|---|---|---|---|
| **P2** | `DecisionLayer`, tests, `adaptive-cost` (scénario coût) | Décider au coût | 1.4.0 |
| **P3** | `PreferenceLearner`, `PreferenceEvaluator`, IPS/ESS, snapshot | Apprendre des rangs | 1.5.0 |

Chaque phase est livrable seule. P2 ne dépend que de 0006 pour la calibration.

## Pièges nommés

1. **Sur-demande.** Le VOI de `Ask` suppose une réponse oracle ; `ResidualCostAfterAsk` évite la
   sur-demande. Le banc mesure `AskRate` et le coût, pas seulement la précision.
2. **Coûts faux.** La couche optimise les coûts qu'on lui donne. Un `AskCost` sous-estimé produit
   une UI qui interrompt tout le temps ; un `AbstainCost` sous-estimé produit une file d'attente.
3. **Calibration périmée.** Une distribution non calibrée rend le coût attendu faux. Réinitialiser
   le calibrateur après un swap de modèle.
4. **Biais de préférence.** Le choix journalisé n'est pas une vérité terrain ; il dépend de la
   politique qui a proposé. IPS corrige si les propensions sont connues ; sinon, on ne prétend rien.
5. **Le 3ᵉ candidat.** Un choix de rang 3 est un signal fort mais rare ; `ByLoggedRank` évite de le
   noyer dans la moyenne.
6. **Fuite de texte dans le modèle de préférence.** En mode vocabulaire, le snapshot contient des
   features de texte, comme `LXCL`. Ce n'est pas un journal, mais c'est un artefact à protéger. En
   mode haché, pas de vocabulaire.
7. **Exploration gelée.** Sans exploration, la politique de préférence ne voit jamais l'alternative
   qu'elle n'a pas proposée. C'est acceptable en P3 ; un bandit est un P4.

## Alternatives rejetées

- **Une seule « smart policy »** qui classe, décide et re-rank : opaque, impossible à tester
  séparément, et un changement de coût forcerait un retrain.
- **Matrice de coûts dense dans un record.** K×K sérialisé, illisible, et la plupart des clients
  n'ont qu'un coût par catégorie réelle. `IMisclassificationCost` couvre les deux.
- **Question en texte libre dans `Decision`.** La bibliothèque ne connaît pas le domaine ; elle
  renvoie les candidats, l'UI formule la question.
- **Bradley-Terry global sur les catégories.** Apprend « A est préféré à B » en moyenne, pas
  « ce texte-ci, dans ce contexte, est B ». Le re-ranker linéaire pairwise est la forme utile.
- **Bandit contextuel en P3.** Exploration, propensions, risque produit ; c'est un P4.
- **DR sans modèle de récompense.** Impossible à estimer ; on ne le prétend pas.
- **Structure temporelle dans la bibliothèque.** Elle dilue l'identité : le client garde l'unité, les
  ruptures et les contraintes.

## Questions ouvertes

1. **Features de préférence** : vocabulaire (explicable, texte dans le snapshot) ou hachage (borné,
   privé, moins explicable) par défaut ? La spec laisse le choix.
2. **Coûts** : un coût par catégorie réelle suffit-il, ou les clients ont-ils besoin d'une matrice
   asymétrique complète ? L'interface le permet.
3. **Exploration** : un P4 avec ε-greedy/Thompson, ou un simple `IExplorationPolicy` suffit-il ?
4. **DR** : un `IRewardModel` optionnel vaut-il le coût, ou IPS + ESS suffisent-ils ?
5. **Feedback au niveau groupe** : la mémoire de 0006 doit-elle recevoir l'unité de travail comme
   contexte, ou l'événement ? La spec recommande l'événement pour le texte et le groupe pour la
   décision ; à valider.

## Différences avec la proposition initiale

1. **`Decision` est structuré, pas une phrase.** `Ask` porte des candidats ; l'UI formule la question.
2. **La matrice de coûts est une interface, pas un record dense.**
3. **Le VOI de `Ask` est explicite et pessimiste** (`ResidualCostAfterAsk`).
4. **`Explore` exige une politique explicite et `ExploreCost` est le coût de l'action**, pas
   `ExploreCost + accept(c)` : sinon l'exploration serait toujours dominée.
5. **`PreferenceLearner` est un re-ranker pairwise, pas un bandit.** Batch est la référence ;
   l'exploration est un P4 ; IPS est l'évaluation, DR est absent sans modèle de récompense.
6. **Le snapshot de préférence est un modèle, pas un journal.** En mode vocabulaire, il contient des
   features de texte, comme `LXCL` ; `HashFeatures` est l'option privée.
7. **Les coûts sont en unités abstraites.** La bibliothèque ne prétend pas connaître les minutes.
8. **La structure temporelle reste chez le client.** C'est la frontière qui garde LexiSharp dans
   « search + measurement » et évite de diluer l'identité.

## Références

- `doc/adr/0006-adaptive-feedback.md` — poids, fraîcheur, mémoire, calibration.
- `src/LexiSharp/Core/ClassificationResult.cs`, `PredictionSet.cs` — la distribution et le set.
- `src/LexiSharp/Classification/ConformalClassifier.cs` — calibration et abstention.
- `src/LexiSharp/Classification/Calibration.cs` — température, log-loss, Brier.
- `src/LexiSharp/Classification/OnlineClassificationEvaluator.cs` — prédire-puis-apprendre,
  bootstrap par blocs, `OrderHash`.
- `src/LexiSharp/Classification/FeatureSet.cs` — `IFeatureSource`, `TextFeatureSource`.
- `src/LexiSharp/Classification/LinearClassifier.cs` — AdaGrad, shuffle semé, L2.
- `src/LexiSharp/Ranking/QueryFeedbackHistory.cs` — la table requête → document existante.
- `src/LexiSharp/Core/IReranker.cs` — le seam du re-ranking.
- Burges et al., *Learning to Rank using Gradient Descent* (RankNet, 2005) — perte pairwise.
- Dudík, Langford, Li (2011) ; Strehl et al. (2010) — IPS/DR pour l'évaluation hors ligne.
