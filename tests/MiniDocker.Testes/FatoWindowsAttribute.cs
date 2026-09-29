namespace MiniDocker.Testes;

/// <summary>
/// Teste que só faz sentido no Windows, por depender do <c>cmd.exe</c> e da API
/// de Job Objects. Em outros sistemas o teste é ignorado em vez de falhar, o que
/// permite desenvolver fora do Windows sem uma suíte vermelha.
/// </summary>
public sealed class FatoWindowsAttribute : FactAttribute
{
    public FatoWindowsAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requer Windows: o container roda via cmd.exe dentro de um Job Object.";
        }
    }
}
