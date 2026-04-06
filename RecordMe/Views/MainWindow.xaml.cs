using RecordMe.Data;
using RecordMe.ViewModels;
using System.Windows;
using Wpf.Ui.Controls;

namespace RecordMe.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel(App.CreateDbContext);
        DataContext = _viewModel;

        Loaded += async (_, _) => await _viewModel.InitializeAsync();
        Closing += (_, _) => _viewModel.Dispose();
    }
}
