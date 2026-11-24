# WolfFlix — extension Jellyfin

Extension serveur du thème [WolfFlix](https://github.com/FenrirRavenswood/wolfflix-theme), pour **Jellyfin 12**.

## Installer
1. Tableau de bord → **Extensions** → **Dépôts** (ou Catalogue → engrenage) → **Ajouter**
   - Nom : `WolfFlix`
   - Adresse : `https://raw.githubusercontent.com/FenrirRavenswood/wolfflix-plugin/main/manifest.json`
2. **Catalogue** → **WolfFlix** → **Installer**, puis redémarrer Jellyfin.
3. Tableau de bord → **WolfFlix** : saisir les clés, puis « Mettre à jour les notes maintenant ».

## Ce qu'elle fait
- **Notes IMDb** (chaque nuit) : note et votes des films, séries et épisodes de la médiathèque, place dans le classement IMDb global et dans le classement WolfFlix. Les fichiers publics d'IMDb sont lus pendant le téléchargement ; seuls les titres de la médiathèque sont gardés (quelques centaines de Ko).

## Adresses (pour le thème)
- `GET /WolfFlix/Etat`
- `GET /WolfFlix/Notes/{id}` — film, série ou épisode
- `POST /WolfFlix/Notes/MettreAJour` — administrateur

## Nouvelle version
Pousser un tag `vX.Y.Z` : GitHub Actions construit la DLL, publie le zip et met à jour `manifest.json`.
