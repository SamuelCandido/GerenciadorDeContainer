namespace MiniDocker.Testes;

/// <summary>
/// Teste do runtime Linux (namespaces + chroot). Só roda num host Linux, e
/// precisa de internet na primeira vez para baixar o alpine do Docker Hub.
/// </summary>
public sealed class FatoLinuxAttribute : FactAttribute
{
    public FatoLinuxAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Requer Linux: o container roda com unshare e chroot.";
        }
    }
}
