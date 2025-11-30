using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ncm2mp3;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly string _ncmdumpPath;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly object _logLock = new object();

    public MainWindow()
    {
        InitializeComponent();
        _dispatcher = Dispatcher.CurrentDispatcher;
        
        // 设置默认输出目录为桌面
        OutputPathTextBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        
        // 检查并设置ncmdump路径
        _ncmdumpPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ncmdump.exe");
        
        // 如果ncmdump.exe不存在，尝试从临时目录复制或下载
        if (!File.Exists(_ncmdumpPath))
        {
            Log("警告: ncmdump.exe 不存在，将在转换时尝试下载。");
        }
    }

    private void BrowseFileButton_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "NCM文件 (*.ncm)|*.ncm|所有文件 (*.*)|*.*",
            Multiselect = true,
            Title = "选择NCM文件"
        };

        if (openFileDialog.ShowDialog() == true)
        {
            SourcePathTextBox.Text = openFileDialog.FileName;
            // 如果用户选择了多个文件，显示第一个文件所在的目录
            if (openFileDialog.FileNames.Length > 1)
            {
                SourcePathTextBox.Text = System.IO.Path.GetDirectoryName(openFileDialog.FileNames[0]) + " (选择了" + openFileDialog.FileNames.Length + "个文件)";
            }
        }
    }

    private void BrowseDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Forms.FolderBrowserDialog folderBrowserDialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择输出目录",
            SelectedPath = OutputPathTextBox.Text
        };

        if (folderBrowserDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            OutputPathTextBox.Text = folderBrowserDialog.SelectedPath;
        }
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(SourcePathTextBox.Text))
        {
            System.Windows.MessageBox.Show("请选择源文件或文件夹", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrEmpty(OutputPathTextBox.Text))
        {
            System.Windows.MessageBox.Show("请选择输出目录", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 检查ncmdump.exe是否存在，如果不存在则尝试下载
        if (!File.Exists(_ncmdumpPath))
        {
            Log("正在下载ncmdump.exe...");
            try
            {
                await DownloadNcmdumpAsync();
                Log("ncmdump.exe下载成功");
            }
            catch (Exception ex)
            {
                Log("错误: 无法下载ncmdump.exe: " + ex.Message);
                System.Windows.MessageBox.Show("无法下载ncmdump.exe，请手动下载并放在程序目录下", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        // 禁用按钮
        ConvertButton.IsEnabled = false;
        ProgressBar.Value = 0;
        LogTextBox.Clear();
        Log("开始转换...");

        // 在启动后台线程前，先获取UI值
        string sourcePath = SourcePathTextBox.Text;
        string outputPath = OutputPathTextBox.Text;
        bool recursive = RecursiveCheckBox.IsChecked ?? false;
        
        // 在后台线程执行转换
        await Task.Run(async () =>
        {
            try
            {
                // 检查源路径是否包含"(选择了"，如果是，则处理目录下的多个文件
                if (sourcePath.Contains("(选择了"))
                {
                    string directoryPath = sourcePath.Split(new[] { " (选择了" }, StringSplitOptions.None)[0];
                    await ProcessDirectoryAsync(directoryPath, outputPath, recursive);
                }
                else
                {
                    // 检查是否是目录
                    if (Directory.Exists(sourcePath))
                    {
                        await ProcessDirectoryAsync(sourcePath, outputPath, recursive);
                    }
                    else if (File.Exists(sourcePath))
                    {
                        // 处理单个文件
                        await ProcessSingleFileAsync(sourcePath, outputPath);
                    }
                    else
                    {
                        Log("错误: 源文件或目录不存在");
                    }
                }
            }
            catch (Exception ex)
            {
                Log("转换过程中发生错误: " + ex.Message);
            }
            finally
            {
                // 恢复按钮状态
                _dispatcher.Invoke(() =>
                {
                    ConvertButton.IsEnabled = true;
                    ProgressBar.Value = 100;
                    Log("转换完成！");
                });
            }
        });
    }

    private async Task ProcessDirectoryAsync(string sourceDir, string outputDir, bool recursive)
    {
        // 获取所有ncm文件
        var ncmFiles = Directory.GetFiles(sourceDir, "*.ncm", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        
        Log($"找到 {ncmFiles.Length} 个NCM文件");
        
        int processedCount = 0;
        foreach (var ncmFile in ncmFiles)
        {
            await ProcessSingleFileAsync(ncmFile, outputDir);
            
            processedCount++;
            double progress = (double)processedCount / ncmFiles.Length * 100;
            _dispatcher.Invoke(() =>
            {
                ProgressBar.Value = progress;
            });
        }
    }

    private async Task ProcessSingleFileAsync(string ncmFilePath, string outputDir)
    {
        Log($"正在处理: {System.IO.Path.GetFileName(ncmFilePath)}");
        
        try
        {
            // 获取DeleteSourceCheckBox的值
            bool deleteSource = false;
            _dispatcher.Invoke(() =>
            {
                deleteSource = DeleteSourceCheckBox.IsChecked ?? false;
            });
            
            // 构建命令行参数
            string arguments = $"\"{ncmFilePath}\" -o \"{outputDir}\"";
            // 不再通过命令行参数删除，而是在程序中安全地处理删除操作
            // if (deleteSource)
            // {  
            //     arguments += " -m"; // 移除这个参数，避免可能的冲突
            // }
            
            // 创建进程
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = _ncmdumpPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            
            using (Process process = new Process { StartInfo = startInfo })
            {
                StringBuilder output = new StringBuilder();
                StringBuilder error = new StringBuilder();
                
                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        output.AppendLine(e.Data);
                    }
                };
                
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        error.AppendLine(e.Data);
                    }
                };
                
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                
                await process.WaitForExitAsync();
                
                if (!string.IsNullOrEmpty(output.ToString()))
                {
                    Log(output.ToString().Trim());
                }
                
                if (!string.IsNullOrEmpty(error.ToString()))
                {
                    Log("错误: " + error.ToString().Trim());
                }
                
                if (process.ExitCode == 0)
                {
                    Log($"成功: {System.IO.Path.GetFileName(ncmFilePath)}");
                    
                    // 如果需要删除源文件
                    if (deleteSource)
                    {
                        try
                        {
                            // 添加重试逻辑，确保文件可以被删除
                            int retryCount = 3;
                            bool deleted = false;
                            
                            for (int i = 0; i < retryCount; i++)
                            {
                                try
                                {
                                    // 先检查文件是否存在
                                    if (File.Exists(ncmFilePath))
                                    {
                                        // 尝试释放文件可能的锁定
                                        using (var fileStream = new FileStream(ncmFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                                        {
                                            // 如果能打开文件，则关闭并删除
                                        }
                                        
                                        File.Delete(ncmFilePath);
                                        Log($"已删除源文件: {System.IO.Path.GetFileName(ncmFilePath)}");
                                        deleted = true;
                                        break;
                                    }
                                    else
                                    {
                                        Log($"源文件不存在: {System.IO.Path.GetFileName(ncmFilePath)}");
                                        deleted = true;
                                        break;
                                    }
                                }
                                catch (IOException)
                                {
                                    // 文件可能被锁定，等待一段时间后重试
                                    if (i < retryCount - 1)
                                    {
                                        await Task.Delay(500);
                                    }
                                }
                            }
                            
                            if (!deleted)
                            {
                                Log($"无法删除源文件（重试多次失败）: {System.IO.Path.GetFileName(ncmFilePath)}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"删除源文件时发生错误: {ex.Message}");
                        }
                    }
                }
                else
                {
                    Log($"失败: {System.IO.Path.GetFileName(ncmFilePath)}，退出码: {process.ExitCode}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"处理文件时发生错误: {ex.Message}");
        }
    }

    private async Task DownloadNcmdumpAsync()
    {
        // 这里使用GitHub上taurusxin/ncmdump的最新版本下载地址
        // 注意：实际使用时可能需要更新下载链接
        string downloadUrl = "https://github.com/taurusxin/ncmdump/releases/latest/download/ncmdump.exe";
        
        using (HttpClient client = new HttpClient())
        {
            // 设置超时
            client.Timeout = TimeSpan.FromMinutes(5);
            
            // 下载文件
            using (Stream stream = await client.GetStreamAsync(downloadUrl))
            using (FileStream fileStream = new FileStream(_ncmdumpPath, FileMode.Create, FileAccess.Write))
            {
                await stream.CopyToAsync(fileStream);
            }
        }
    }

    private void Log(string message)
    {
        string logMessage = $"[{DateTime.Now:HH:mm:ss}] {message}";
        
        lock (_logLock)
        {
            _dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText(logMessage + Environment.NewLine);
                LogTextBox.ScrollToEnd();
            });
        }
    }
}