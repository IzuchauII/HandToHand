using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using LLama;
using LLama.Common;
using LLama.Native;

namespace HandToHand
{

    public partial class MainWindow : Window
    {
        //Параметры модели(путь к файлу модели, размер контекста, количество потоков и т.д.)
        private ModelParams _modelParams = null!;
        //Сама модель, которая будет использоваться для генерации ответов на основе пользовательского ввода
        private LLamaWeights _model = null!;
        //Сессия контекста, которая будет использоваться для хранения состояния диалога и генерации ответов на основе пользовательского ввода
        private LLamaContext _context = null!;
        //Исполнитель, который будет использоваться для генерации ответов на основе пользовательского ввода и текущего состояния диалога
        private ChatSession _session = null!;
        public MainWindow()
        {
            InitializeComponent();

            // Запускаем инициализацию ИИ
            InitAiModel();
        }

        private void InitAiModel()
        {
            // 1. Указываем путь к файлу модели и размер контекста (сколько токенов она помнит)
            string modelPath = @"D:\AiModels\qwen2.5-1.5b-instruct-q8_0.gguf";

            // Создаем объект настроек
            _modelParams = new ModelParams(modelPath)
            {
                ContextSize = 2048, // Модель будет помнить примерно 1500 слов диалога
                GpuLayerCount = 0   // Пока оставляем 0 (работа на CPU). Позже включим видеокарту!
            };

            // 2. Физически загружаем файл модели в оперативную память (это может занять 2-3 секунды)
            _model = LLamaWeights.LoadFromFile(_modelParams);

            // 3. Создаем контекст для вычислений
            _context = _model.CreateContext(_modelParams);

            // 4. Обертываем всё это в удобную сессию чата
            _session = new ChatSession(new InteractiveExecutor(_context));

        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
        string userText = UserInput.Text.Trim();
            if (string.IsNullOrEmpty(userText))
            {
                return;
            }
        ChatLog.Text += "\n\nВы: " + userText; 
        UserInput.Text = "";

            try
            {
                // 3. Отправляем текст в сессию нейросети асинхронно
                // Слово await говорит: "Жди ответа тут в фоне, не блокируя интерфейс"
                await foreach (var token in _session.ChatAsync(new ChatHistory.Message(AuthorRole.User, userText), new InferenceParams() { MaxTokens = 512 }))
                {
                    // Получаем ответ по одному слову (токену) и сразу дописываем его в окно чата на лету!
                    ChatLog.Text += token;

                    // 2. АВТОСКРОЛЛ: Приказываем скроллеру прыгнуть в самый низ до упора
                    ChatHistoryScrollViewer.ScrollToEnd();

                }
            }
            catch (System.Exception ex)
            {
                // Если что-то пойдет не так (например, закончится память) — выведем ошибку
                ChatLog.Text += "\n[Ошибка генерации: " + ex.Message + "]";
            }
        }
    }
}