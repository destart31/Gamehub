using System.Windows;
using System.Windows.Threading;
using GameHub.Services;

namespace GameHub;

public partial class App : Application
{
    // Se os erros se repetirem em sequência (ex.: um erro dentro de um timer), fechar é melhor que ficar em laço
    private const int MaxErrorsInBurst = 3;
    private static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(5);
    private readonly Queue<DateTime> _recentErrors = new();

    public App()
    {
        // Erro na interface (clique, animação, timer do Dispatcher): o programa continua aberto
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Erro em thread de fundo: o .NET encerra o programa, mas antes fica registrado
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.LogError("Erro fatal (AppDomain)", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));

        // Task que falhou e ninguém esperou (ex.: carregamento em segundo plano): registra e segue
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.LogError("Tarefa em segundo plano falhou", e.Exception);
            e.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.LogError("Erro na interface", e.Exception);

        var now = DateTime.Now;
        _recentErrors.Enqueue(now);
        while (_recentErrors.Count > 0 && now - _recentErrors.Peek() > BurstWindow)
            _recentErrors.Dequeue();

        e.Handled = true; // evita fechar o programa

        if (_recentErrors.Count >= MaxErrorsInBurst)
        {
            MessageBox.Show(
                "O GameHub encontrou erros repetidos e vai fechar para evitar problemas.\n\n" +
                "Os detalhes foram salvos em %AppData%\\GameHub\\errors.log",
                "GameHub", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Só mostra o aviso na primeira ocorrência da sequência, para não empilhar janelas
        if (_recentErrors.Count == 1)
        {
            MessageBox.Show(
                "Algo deu errado, mas o GameHub continua aberto.\n\n" +
                $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n" +
                "Os detalhes foram salvos em %AppData%\\GameHub\\errors.log",
                "GameHub", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
