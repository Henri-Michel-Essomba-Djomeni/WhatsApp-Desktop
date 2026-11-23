# WhatsApp Desktop (clone P2P, C# / WPF), Powered by nOX-00

Projet individuel : clone fidèle de WhatsApp Desktop, messagerie P2P réelle, sans serveur central.
SDK .NET 11, Windows uniquement. Dépôt : github.com/Henri-Michel-Essomba-Djomeni/WhatsApp-Desktop

## Solution
- WhatsApp.Core (classlib) : Models/Models.cs, Storage/Database.cs, Network/{Packet,PeerConnection,P2PNode}.cs
- WhatsApp.App (WPF) : App.xaml(.cs), LoginWindow, MainWindow, Session.cs, ViewModels.cs, Dialogs.cs, Theme.cs
- WhatsApp.Test (console) : test réseau sans interface

## Architecture
- Identité = numéro de téléphone. Code de vérification affiché localement (pas de vrai SMS).
- P2P : découverte par broadcast UDP (port 45454), connexion TCP directe, trames = 4 octets de longueur + JSON (Packet).
- Anti-doublon : seul le plus petit numéro initie la connexion.
- Messages hors ligne : statut Pending, renvoyés automatiquement à la reconnexion du contact.
- Coches : Pending(○) Sent(✓) Delivered(✓✓) Read(✓✓ bleues). Le statut ne peut que monter.
- Stockage : SQLite par numéro dans %AppData%\WhatsAppClone\<numéro>\ (chat.db + dossier media). Les médias sont des fichiers, la base ne garde que le chemin.
- Database : toutes les méthodes publiques prennent lock(_gate) (appelée depuis plusieurs threads).
- Les événements de P2PNode arrivent sur des threads de fond : toujours Dispatcher.InvokeAsync dans l'interface.

## Conventions
- Couleurs via DynamicResource (App.xaml + Theme.cs pour le thème clair/sombre).
- Icônes : police Segoe MDL2 Assets.
- Lancer deux instances : dotnet build, puis dotnet run --project WhatsApp.App --no-build dans deux terminaux (numéros différents).
- Fermer toutes les fenêtres avant de recompiler (fichier verrouillé).

## État
Fait : messages, coches, écrit..., en ligne, emojis, images, fichiers, répondre/copier/supprimer, vocaux avec ondes, paramètres (nom, info, photo, thème), stickers.
À faire : statuts 24 h, appels vocal puis vidéo, groupes, recherche dans une discussion, finitions de fidélité visuelle, README.
Décision : l'envoi vers de vrais comptes WhatsApp est écarté (API officielle Business seulement, bibliothèques non officielles interdites), présenté comme évolution possible.