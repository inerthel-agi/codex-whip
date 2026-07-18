# ADR 001: application Windows native WPF

- Status: Accepted
- Date: 2026-07-17

## Context

OpenWhip est une application Electron qui affiche un overlay et envoie `Ctrl+C`
à un terminal Claude Code. Codex Desktop reçoit plutôt les relances dans le
compositeur de la tâche active. Le processus Desktop actuel expose son app-server
à son processus parent par `stdio`, pas comme un endpoint externe auquel un petit
utilitaire peut s'attacher sans couplage privé.

## Decision

Créer un MVP Windows natif en .NET 8/WPF, sans dépendance tierce. L'application
utilise :

- WPF pour l'overlay transparent et le rendu du fouet ;
- Windows Forms pour l'icône de notification ;
- Windows UI Automation pour reconnaître la tâche et le compositeur Codex ;
- les API de console Windows pour vérifier une session Codex CLI dans PowerShell
  classique ou l'invite de commandes ;
- `SendInput` uniquement après vérification du processus, du focus, du tour actif
  et du brouillon.

Le portage envoie un message de steering au lieu d'annuler le tour avec `Ctrl+C`.
Il ne remplace pas le binaire Codex et ne modifie aucune configuration Codex.

## Consequences

- Le MVP est limité à Windows.
- L'exécutable reste léger et ne requiert ni Electron ni module natif externe.
- La détection UI devra être revue si l'arbre d'accessibilité de Codex change.
- La V1 CLI ne prend pas en charge Windows Terminal, VS Code, Git Bash ou WSL.
- Un port macOS futur devra remplacer UI Automation et `SendInput` sans modifier
  le comportement fonctionnel du fouet.
