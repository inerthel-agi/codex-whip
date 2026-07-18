# Codex Whip

![Bannière Codex Whip](docs/assets/codex-whip-banner.png)

Un petit utilitaire Windows inspiré par
[OpenWhip](https://github.com/GitFrog1111/OpenWhip) pour Codex
Desktop et Codex CLI : un fouet apparaît au-dessus de l'écran et son claquement
envoie une relance à la tâche Codex active.

Codex Whip fonctionne en arrière-plan depuis la zone de notification. Il ne
modifie ni la configuration ni les sessions de Codex.

Codex Whip est un projet communautaire indépendant. Il n'est ni affilié à
OpenAI, ni approuvé par OpenAI.

[Télécharger la dernière version Windows](https://github.com/stealthsrc/codex-whip/releases/latest)

## Démarrage rapide

1. Ouvre Codex Desktop ou démarre Codex CLI dans PowerShell classique ou
   l'invite de commandes.
2. Lance `CodexWhip.exe`. Le fouet reste masqué au démarrage.
3. Appuie sur `F8` pour afficher le fouet.
4. Fais un aller-retour rapide avec la souris pour provoquer un claquement.
5. Appuie de nouveau sur `F8` pour ranger le fouet.

Pour Codex Desktop, règle **Settings > General > Follow-up behavior** sur
**Steer**. Avec **Queue**, le message attendra la fin de la tâche en cours.

## Fonctionnement

- L'application reste dans la zone de notification Windows.
- Un clic gauche sur l'icône affiche ou range le fouet.
- `F8` affiche ou range le fouet depuis n'importe quelle application. Le
  sous-menu **Whip shortcut** permet de choisir `F8`, `F9`, `F10` ou
  `Ctrl+Alt+W` pour la session.
- Le steering manuel prend en charge Codex Desktop et une session Codex CLI
  officielle au premier plan dans PowerShell classique ou l'invite de commandes.
- Le menu **Steering automatique** peut envoyer une relance toutes les 60
  secondes lorsque Codex Desktop ou une console Codex CLI compatible est déjà
  au premier plan. Il reste désactivé à chaque démarrage.
- La force du fouet suit la vitesse de la souris : un mouvement lent reste
  souple, tandis qu'un aller-retour assez rapide le fait claquer et envoie un
  message de steering au travail en cours.
- Les fichiers `sounds\whip_1.wav` à `sounds\whip_4.wav` correspondent aux
  quatre niveaux de puissance et sont lisibles sans codec externe.
- Le clic gauche dans l'overlay ne ferme plus le fouet.

Codex Whip ne modifie ni `config.toml`, ni `CODEX_CLI_PATH`, ni les fichiers de
session Codex. Il cible la fenêtre `ChatGPT.exe` installée par le package Windows
officiel `OpenAI.Codex` ou une session officielle Codex CLI déjà ouverte.

## Menu de la zone de notification

- **Whip Codex** affiche ou range le fouet.
- **Steering automatique** active une tentative de relance toutes les 60
  secondes. L'option est désactivée à chaque démarrage.
- **Whip shortcut** choisit `F8`, `F9`, `F10` ou `Ctrl+Alt+W`. Le choix vaut
  pour la session actuelle ; `F8` redevient la valeur par défaut au redémarrage.
- **Quitter** libère le raccourci global et ferme l'application.

Si un raccourci est déjà utilisé par une autre application, Codex Whip conserve
le raccourci précédent et affiche un avertissement.

## Compatibilité Codex CLI V1

Le claquement manuel peut envoyer le message de steering à Codex CLI dans une
console **PowerShell classique** ou **Invite de commandes**. Avant tout envoi,
Codex Whip vérifie que la console est au premier plan, qu'une seule session
Codex CLI officielle est présente, qu'un tour est actif et que le compositeur
est vide, sans brouillon ni pièce jointe. Une sélection de console ou une touche
de modification maintenue bloque aussi l'envoi. Si l'un de ces garde-fous ne
peut pas être vérifié, aucun texte n'est envoyé.

Après l'envoi, Codex Whip appuie sur `Esc` uniquement si l'aperçu exact du steer
en attente est visible, afin de l'appliquer immédiatement. Windows Terminal, le
terminal intégré de VS Code, Git Bash et WSL ne sont pas pris en charge en V1.

Le steering automatique n'ouvre pas Codex et ne lui vole pas le focus. Il
effectue une tentative uniquement si Codex Desktop ou une console CLI compatible
est déjà au premier plan, puis ignore silencieusement les situations à risque.

## Langues

L'interface suit automatiquement la langue système. Dix-huit langues sont
intégrées : anglais, français, espagnol, allemand, italien, portugais,
néerlandais, polonais, russe, ukrainien, turc, arabe, hindi, indonésien,
japonais, coréen, chinois simplifié et chinois traditionnel. Toute autre langue
utilise l'anglais. Le message de steering reste volontairement court et en
anglais universel.

## Prérequis

- Windows 10 ou Windows 11.
- Codex Desktop ouvert ou une session Codex CLI active dans PowerShell classique
  ou l'invite de commandes.
- .NET 8 Desktop Runtime.
- Pour Codex Desktop : **Settings > General > Follow-up behavior > Steer**.

## Construire et lancer

```powershell
dotnet build -c Release
dotnet run -c Release
```

Pour lancer directement avec le fouet visible :

```powershell
dotnet run -c Release -- --show
```

Pour publier l'exécutable framework-dependent :

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

Le résultat est placé dans
`bin\Release\net8.0-windows\win-x64\publish\CodexWhip.exe`. Les quatre sons WAV
sont publiés à côté de l'exécutable dans le dossier `sounds`.

## Options de ligne de commande

| Option        | Effet                                                                                                                    |
| ------------- | ------------------------------------------------------------------------------------------------------------------------ |
| `--show`      | Lance l'application avec le fouet visible.                                                                               |
| `--diagnose`  | Vérifie Codex Desktop et écrit un résultat JSON. Retourne `0` si l'intégration est prête, sinon `2`.                     |
| `--self-test` | Teste le geste, les sons, les langues, les garde-fous CLI et les raccourcis. Retourne `0` en cas de réussite, sinon `3`. |

Exemple de diagnostic :

```powershell
dotnet run -c Release -- --diagnose
```

## Tests

Le détecteur de geste, les quatre niveaux sonores, les traductions et les
garde-fous CLI possèdent un test déterministe :

```powershell
dotnet run -c Release -- --self-test
```

## Garde-fous

- Aucun texte n'est envoyé si la fenêtre officielle n'est pas reconnue.
- Aucun texte n'est envoyé sans tâche active.
- Aucun texte n'est envoyé si le focus n'appartient pas à Codex.
- Un brouillon existant est toujours conservé et bloque le coup.
- Pour Codex CLI, la console doit être au premier plan et une seule session
  officielle avec un tour actif et un compositeur vide doit être vérifiable.
- Deux messages ne peuvent pas être envoyés à moins de 1,4 seconde d'intervalle.

## Résoudre les problèmes courants

| Problème                    | Vérification                                                                                                         |
| --------------------------- | -------------------------------------------------------------------------------------------------------------------- |
| `F8` ne répond pas          | Choisis un autre raccourci dans **Whip shortcut** ; une autre application utilise peut-être `F8`.                    |
| Le fouet bouge sans claquer | Fais un aller-retour plus rapide et marqué. Un mouvement lent ou dans une seule direction ne déclenche rien.         |
| Aucun son n'est joué        | Vérifie que `sounds\whip_1.wav` à `sounds\whip_4.wav` se trouvent à côté de l'exécutable publié.                     |
| Desktop ne reçoit rien      | Garde une tâche active avec le bouton **Stop**, vide le compositeur et sélectionne le mode **Steer**.                |
| CLI ne reçoit rien          | Utilise PowerShell classique ou l'invite de commandes, garde un seul tour Codex actif et laisse le compositeur vide. |
| Un message est bloqué       | Ferme toute sélection de console, relâche les touches de modification et attends la fin du délai anti-double envoi.  |

## Limite connue

L'intégration Desktop utilise Windows UI Automation et simule une saisie clavier
uniquement après ses vérifications. Une évolution importante de l'interface
Codex Desktop peut nécessiter d'ajuster la détection du compositeur. La V1 CLI
reste limitée aux consoles Windows classiques listées ci-dessus.
