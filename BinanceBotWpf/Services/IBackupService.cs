using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public interface IBackupService
    {
        Task CheckAndBackupAsync ();
        Task CreateBackupAsync ();
        string[] GetAvailableBackups ();
        Task<bool> RestoreFromBackupAsync (string backupPath);
    }
}
