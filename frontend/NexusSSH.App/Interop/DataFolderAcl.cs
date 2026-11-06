using System.Security.AccessControl;
using System.Security.Principal;
using NexusSSH.Core.Services;

namespace NexusSSH.App.Interop;

/// <summary>Liest die Zugriffsliste eines Ordners für <see cref="DataFolderCheck"/>.</summary>
internal static class DataFolderAcl
{
    /// <summary>
    /// Wer außer dem Benutzer und den Verwaltern des Rechners im Ordner schreiben darf. Leer auch dann, wenn sich
    /// das nicht feststellen lässt (ein Laufwerk ohne Zugriffslisten wie ein USB-Stick mit FAT, eine Freigabe, die
    /// sie nicht nennt) – dort gibt es nichts zu prüfen.
    /// </summary>
    public static IReadOnlyList<string> OthersWhoCanWrite(string folder)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is not { } own)
            {
                return [];
            }

            // Beim ersten Start gibt es den Ordner noch nicht – die App legt ihn gleich an, und er erbt, was im
            // Ordner darüber gilt. Ohne das bliebe der erste Start ungeprüft (gefunden 2026-10-12).
            Directory.CreateDirectory(folder);

            var rules = new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Access).GetAccessRules(true, true, typeof(SecurityIdentifier));
            var grants = new List<FolderGrant>();
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference is SecurityIdentifier sid)
                {
                    grants.Add(new FolderGrant(sid.Value, NameOf(sid), (int)rule.FileSystemRights));
                }
            }

            return DataFolderCheck.OthersWhoCanWrite(grants, own.Value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SystemException)
        {
            return [];
        }
    }

    private static string NameOf(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (Exception e) when (e is IdentityNotMappedException or SystemException)
        {
            return sid.Value;
        }
    }
}
