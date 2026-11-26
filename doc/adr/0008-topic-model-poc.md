# ADR 0008 — Un modèle de topics pour la classification multi-prototypes

- **Statut** : proposé
- **Date** : 2026-11-26
- **Dépôt** : LexiSharp
- **Série** : expérience séparée, alternative à l'idée « Mixture of Multinomial Naive Bayes »
  (MMNB) pour le texte court.
- **Portée** : POC borné, hors du cœur et du chemin par défaut.

## Contexte

Le Naive Bayes multinomial suppose une seule distribution lexicale par classe. Une classe peut
pourtant contenir plusieurs sous-univers cohérents — `PROGRAMMING` = `.NET` (C#, CLR, NuGet,
MSBuild), Web (HTTP, REST, OAuth, JWT), Infrastructure (Kubernetes, Docker, Helm) — dont les
vocabulaires ne se recouvrent pas. NB dilue les associations locales (`C#`–`NuGet`,
`Kubernetes`–`Helm`) et ne modélise pas les dépendances entre mots.

Le corpus où cette limite se voit est le **texte court**. Sur le pin 20 Newsgroups subject-only :

| Modèle | accuracy / macro-F1 | McNemar vs NB |
|---|---:|---|
| Naive Bayes | **0.5624 / 0.5293** | — |
| Linéaire | 0.5533 / 0.5295 | p = 0.16 |
| kNN (BM25/dense) | 0.5073 / 0.4890 | p = 1.3e-12 (contre kNN) |

Sur le corpus complet, le linéaire domine largement (0.8085 / 0.8027 contre 0.7573 / 0.7289 pour
NB). La niche du sujet est donc réelle mais étroite, et le barreau à battre y est **NB**, pas le
linéaire.

Deux familles de modèles attaquent cette limite : un mélange supervisé par classe (MMNB), et un
**modèle de topics** non supervisé (LDA) dont les proportions servent de features à un classifieur.
La décision retenue est de commencer par LDA.

## Décision

**POC borné : LDA comme transformateur de features, plus une tête linéaire ; MMNB conservé comme
baseline d'attribution.**

Le point décisif est que LDA et MMNB ne testent pas la même hypothèse. Il faut l'écrire pour ne pas
attribuer un gain au mauvais mécanisme :

| Approche | Hypothèse testée |
|---|---|
| MMNB, LDA conditionnelle aux classes | « une classe contient plusieurs sous-univers lexicaux » |
| LDA + tête | « une représentation de topics non supervisée aide le classifieur » |

LDA reste une **expérience séparée** : ni le cœur de la bibliothèque, ni le chemin par défaut, ni un
nouveau `ModelKind` avant un gain mesuré. S'il gagne, il devient une lane optionnelle de
`BlendTextClassifier`, jamais un remplacement du linéaire.

## Architectures considérées

| Architecture | Supervisé | Inférence | Comparabilité |
|---|---|---|---|
| **LDA + tête linéaire** | non | fold-in par document | baseline naturelle : NB/linéaire sur les mêmes θ |
| Labeled LDA | oui (topics contraintes par label) | marginalisation sur 2^C ensembles de labels | proche de MMNB, inférence combinatoire |
| LDA conditionnelle aux classes | oui (un modèle par classe) | fold-in par classe | K=1 ≈ NB, K>1 ≈ MMNB en plus lourd |

**Retenu : LDA + tête linéaire.** C'est le seul choix qui apporte une représentation réellement
neuve, et le moins cher à inférer. Labeled LDA bute sur 2^20 ensembles de labels dès 20 classes ;
la LDA conditionnelle réimplémente MMNB avec une inférence en plus.

## Risque principal : le court texte

Un sujet fait 5 à 10 tokens. LDA estime des co-occurrences au niveau document ; sur des documents
aussi courts, θ reste proche de l'uniforme et les topics ne se séparent pas. C'est la raison d'être
de BTM, DMM ou Gaussian LDA dans la littérature court-texte.

Conséquence : un LDA vanilla peut perdre **par inadéquation au corpus**, pas parce que l'idée est
fausse. Le protocole doit donc produire un diagnostic **avant** le score :

- `max_k θ_k` moyen sur les sujets, comparé aux corps de message ;
- NMI / ARI entre topic dominant et vraie classe.

Critère d'abandon écrit d'avance : si `max θ < 2/K` ou NMI < 0.1 sur subject-only, on arrête sans
tuner.

## Seam minimal

Trois pièces, dont deux existent déjà dans le dépôt :

```csharp
// 1. Nouveau : modèle de topics, transformateur — pas un classifieur.
public sealed class LatentDirichletAllocation
{
    public LatentDirichletAllocation(LdaOptions? options = null, ITokenizer? tokenizer = null);

    public void Fit(IEnumerable<SearchDocument> documents);   // non supervisé, labels ignorés
    public TopicVector Transform(string text);                // fold-in, jamais de refit
    public IReadOnlyList<TopicTerms> TopTerms(int perTopic);
    public double Perplexity(IEnumerable<SearchDocument> documents);
}

// 2. Existant : la tête. LinearClassifier<TItem> + IFeatureSet.
public sealed class TopicFeatureSet : IFeatureSet
{
    // une feature "topic.k" par topic, valeur numérique θ_k, jamais NaN
}

// 3. Nouveau : façade fine, pour ITextClassifier.
public sealed class LdaTopicClassifier : ITextClassifier, IExplainableTextClassifier
{
    // Fit du LDA sur le train, Train de la tête sur les θ du train
    // Predict = Transform puis tête
}
```

Options : `K` fixé (20 / 50 / 100), `Alpha` et `Beta` fixés, `MaxIterations`, `Tolerance`, `Seed`.
Inférence variationnelle batch (déterministe) plutôt que Gibbs ; le seed ne sert qu'à casser la
symétrie initiale.

**Explication.** `ClassificationExplanation` porte déjà `Lanes` et `Terms`. Avec une tête linéaire,
la contribution d'un terme est exacte : `Σ_k θ_k · w_ck · φ_kw`. C'est un avantage réel sur MMNB, où
l'explication est par composante.

**Persistance.** Hors POC. Un modèle composé (φ, α, β, poids de tête) demande un nouveau
`ModelKind` LXCL et un bump de version.

**Incrémental.** Non : pas de `IIncrementalTextClassifier`, pas de `Reinforce`. L'inférence n'est
pas exactement inversible ; `AdaptiveClassifier` gère le swap/retrain, comme pour les autres
modèles non incrémentaux.

## Invariants

1. `θ ≥ 0`, `Σθ = 1` à 1e-9 ; `φ_k ≥ 0`, `Σ_w φ_kw = 1`.
2. `Fit` ne lit **aucune** catégorie. Le protocole principal ajuste sur le train seul et passe le
   test par fold-in ; une variante transductive (fit sur train + test) est un chiffre séparé,
   jamais le chiffre principal.
3. Déterminisme : même seed, même ordre de documents, même nombre d'itérations → même modèle et
   même perplexité.
4. Document vide → θ = α / Σα, sans NaN ; mots hors vocabulaire ignorés avec un taux loggé.
5. `K = 1` → un topic, θ = 1 ; la tête dégénère en biais seul. C'est un sanity check, pas une
   équivalence avec NB.
6. `K > |V|` → topics dégénérés, pas de division par zéro ; perplexité finie.
7. Aucune mutation pendant la prédiction ; `Fit` remplace l'état.

## Tests

- **Corpus synthétique** 2 classes × 2 sous-univers disjoints : le classifieur doit récupérer les
  sous-univers ; NMI topics/labels ≥ seuil.
- **Déterminisme** : deux fits, même seed ; seed différent → modèle différent, performances
  comparables.
- **Formes** : θ row-stochastique, φ normalisé, pas de NaN sur document vide ou vocabulaire
  disjoint.
- **Fold-in** : ne voit pas les labels ; écart de performance borné contre un refit.
- **Contrôle d'attribution** : NB et linéaire entraînés sur les **mêmes θ** contre les comptes
  bruts. Si la tête sur θ ne fait pas mieux que le linéaire sur les mots, LDA n'apporte rien.
- **Diagnostic court-texte** : `max θ` sujet vs corps, NMI.
- Cas limites : `K = 1`, `K > |V|`, document vide, topic mort.

## Benchmark

- **20NG full d'abord** (sanity, où LDA est connu pour fonctionner), **puis subject-only**
  (`--text subject`), la niche.
- Baselines : NB, linéaire, MMNB, kNN. Le critère d'adoption sur subject est **NB**.
- `K ∈ {20, 50, 100}`, `α = 0.1`, `β = 0.01`, itérations bornées ; aucun tuning sur le test.
- Accuracy, macro-F1, log-loss/Brier **après calibration** (`ConformalClassifier`,
  `CalibratedClassifier`).
- McNemar apparié via `ClassificationStatistics` ; pins `classification-20news-lda.json` et
  `classification-20news-subject-lda.json`.
- Coût : temps de fit, **latence de prédiction** (fold-in itératif contre un seul passage NB),
  mémoire, taille du modèle.
- Les pertes sont publiées, comme pour kNN.

## Go / No-Go

- **Go** pour le POC borné décrit ci-dessus, avec MMNB en baseline d'attribution.
- **No-Go** pour Labeled LDA et la LDA conditionnelle dans ce POC.
- **No-Go** pour le cœur, le chemin par défaut, la sérialisation, et toute adoption avant McNemar.
- **No-Go** pour le graphe de cooccurrence / factor graph : expérience distincte, coût O(V²) ou
  graphe sparse, risque de sur-apprentissage non mesuré.

## Conséquences

- MMNB reste le plus petit seam pour l'hypothèse « sous-univers lexicaux » ; LDA teste une autre
  hypothèse et ne le remplace pas.
- Si LDA gagne sur subject-only, il devient une lane optionnelle ; le linéaire reste le défaut du
  corpus complet.
- Si LDA perd ou reste plat, le résultat est publié avec le diagnostic `max θ` / NMI, pour
  distinguer « l'idée est fausse » de « le modèle est inadapté au court texte ».
