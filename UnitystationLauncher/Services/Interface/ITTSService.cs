using System.Threading.Tasks;
using UnitystationLauncher.Models;

namespace UnitystationLauncher.Services.Interface;

public interface ITTSService
{
    public Task DownloadLatest(Download Download);

    public void StartTTS();
    public void StopTTS();
}