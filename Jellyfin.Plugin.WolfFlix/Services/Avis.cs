using System.Text.Json;

namespace Jellyfin.Plugin.WolfFlix.Services;

/// <summary>L'avis d'un utilisateur sur un film ou une série.</summary>
public sealed class AvisUtilisateur
{
    /// <summary>Les 5 critères, de 0 à 5 : scénario, personnages, image, ambiance, impact.</summary>
    public double[] Criteres { get; set; } = new double[5];

    public string Commentaire { get; set; } = string.Empty;

    public DateTime Date { get; set; }

    /// <summary>Note finale : moyenne exacte des critères, sur 5.</summary>
    public double Note => Criteres.Length == 0 ? 0 : Criteres.Average();
}

/// <summary>Avis du WolfFlix, partagés entre tous les utilisateurs du serveur. Stockés dans avis.json.</summary>
public sealed class Avis
{
    public const int NbCriteres = 5;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly object _verrou = new();
    private Dictionary<string, Dictionary<string, AvisUtilisateur>>? _tous;

    private static string Chemin
    {
        get
        {
            var d = Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "wolfflix");
            Directory.CreateDirectory(d);
            return Path.Combine(d, "avis.json");
        }
    }

    private Dictionary<string, Dictionary<string, AvisUtilisateur>> Tous
    {
        get
        {
            if (_tous != null)
            {
                return _tous;
            }

            try
            {
                _tous = File.Exists(Chemin)
                    ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, AvisUtilisateur>>>(File.ReadAllText(Chemin), Json)
                    : null;
            }
            catch (Exception)
            {
                // Fichier abîmé : on le garde de côté plutôt que de l'écraser
                try
                {
                    File.Copy(Chemin, Chemin + ".abime-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture), true);
                }
                catch (Exception)
                {
                    // rien
                }
            }

            _tous ??= new Dictionary<string, Dictionary<string, AvisUtilisateur>>();
            return _tous;
        }
    }

    /// <summary>Avis d'un titre (copie), par identifiant d'utilisateur.</summary>
    public Dictionary<string, AvisUtilisateur> De(Guid item)
    {
        lock (_verrou)
        {
            return Tous.TryGetValue(item.ToString("N"), out var d) ? new Dictionary<string, AvisUtilisateur>(d) : new Dictionary<string, AvisUtilisateur>();
        }
    }

    /// <summary>Moyenne des notes de chaque titre noté (pour les classements).</summary>
    public Dictionary<string, (double Moyenne, int Nb)> Moyennes()
    {
        lock (_verrou)
        {
            return Tous.Where(kv => kv.Value.Count > 0)
                .ToDictionary(kv => kv.Key, kv => (kv.Value.Values.Average(a => a.Note), kv.Value.Count));
        }
    }

    public void Enregistrer(Guid item, Guid utilisateur, double[] criteres, string? commentaire)
    {
        lock (_verrou)
        {
            var cle = item.ToString("N");
            if (!Tous.TryGetValue(cle, out var d))
            {
                Tous[cle] = d = new Dictionary<string, AvisUtilisateur>();
            }

            d[utilisateur.ToString("N")] = new AvisUtilisateur
            {
                Criteres = criteres.Select(c => Math.Clamp(Math.Round(c * 2, MidpointRounding.AwayFromZero) / 2, 0, 5)).ToArray(),
                Commentaire = Couper((commentaire ?? string.Empty).Trim(), 4000),
                Date = DateTime.UtcNow
            };
            Sauver();
        }
    }

    public bool Supprimer(Guid item, Guid utilisateur)
    {
        lock (_verrou)
        {
            var cle = item.ToString("N");
            if (!Tous.TryGetValue(cle, out var d) || !d.Remove(utilisateur.ToString("N")))
            {
                return false;
            }

            if (d.Count == 0)
            {
                Tous.Remove(cle);
            }

            Sauver();
            return true;
        }
    }

    private static string Couper(string t, int max) => t.Length > max ? t[..max] : t;

    private void Sauver()
    {
        var tmp = Chemin + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Tous, Json));
        File.Move(tmp, Chemin, true);
    }
}
