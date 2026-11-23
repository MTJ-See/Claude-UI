using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClaudeUI
{
    public sealed class StoredMessage
    {
        public string Role { get; set; }
        public string Text { get; set; }
        public ChatRunSummary RunSummary { get; set; }
        public List<ChatAttachment> Attachments { get; set; }
        // Transient source segments let history import match the live streamed reply.
        internal List<string> CliReplyParts;
    }

    public sealed class Conversation : System.ComponentModel.INotifyPropertyChanged
    {
        public string Title { get; set; }
        public string Id { get; set; }
        public string Workspace { get; set; }
        public string SessionId { get; set; }
        public string CliConfigDirectory { get; set; }
        public string ModelProfileId { get; set; }
        public List<StoredMessage> Messages { get; set; }
        public string UpdatedUtc { get; set; }
        [ScriptIgnore] public string CliTranscriptPath { get; set; }
        [ScriptIgnore] public string Draft { get; set; }
        public List<ChatAttachment> DraftAttachments { get; set; }
        internal string FirstPrompt, FirstApiError, FirstApiErrorUtc;
        private bool taskRunning, navigationLocked;
        [ScriptIgnore] public bool IsTaskRunning { get { return taskRunning; } }
        [ScriptIgnore] public bool CanNavigate { get { return !navigationLocked; } }
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        internal void SetExecutionState(bool active, bool locked)
        {
            bool changed = taskRunning != active || navigationLocked != locked;
            taskRunning = active; navigationLocked = locked;
            if (changed && PropertyChanged != null)
            {
                PropertyChanged(this, new System.ComponentModel.PropertyChangedEventArgs("IsTaskRunning"));
                PropertyChanged(this, new System.ComponentModel.PropertyChangedEventArgs("CanNavigate"));
            }
        }


        public Conversation()
        {
            Id = Guid.NewGuid().ToString();
            Messages = new List<StoredMessage>();
        }
    }

    public sealed class ProjectNavigationState
    {
        public string Folder { get; set; }
        public string Name { get; set; }
        public bool IsPinned { get; set; }
        public bool IsArchived { get; set; }
        public bool IsRemoved { get; set; }
    }

    public sealed class AppData
    {
        public bool Dark { get; set; }
        public string MarketplaceModelScopeToken { get; set; }
        public string MarketplaceGitHubToken { get; set; }
        public double SidebarWidth { get; set; }
        public double RequestDetailWidth { get; set; }
        public double RequestDetailHeight { get; set; }
        public bool ProjectsCollapsed { get; set; }
        public bool ConversationsCollapsed { get; set; }
        public List<string> ProjectFolders { get; set; }
        public List<ProjectNavigationState> ProjectStates { get; set; }
        public List<string> CollapsedProjects { get; set; }
        public List<string> HiddenCliSessions { get; set; }
        public string CliPath { get; set; }
        public string Workspace { get; set; }
        public string ActiveConversationId { get; set; }
        public string Model { get; set; }
        public string Effort { get; set; }
        public string Permission { get; set; }
        public List<Conversation> Conversations { get; set; }
        public List<ModelProfile> ModelProfiles { get; set; }
        public List<RequestRate> RequestRates { get; set; }

        public AppData()
        {
            SidebarWidth = 240;
            RequestDetailHeight = 220;
            ProjectFolders = new List<string>();
            ProjectStates = new List<ProjectNavigationState>();
            CollapsedProjects = new List<string>();
            HiddenCliSessions = new List<string>();
            Conversations = new List<Conversation>();
            ModelProfiles = new List<ModelProfile>();
            RequestRates = new List<RequestRate>();
        }

        public void MigrateModel()
        {
            if (ProjectFolders == null) ProjectFolders = new List<string>();
            if (ProjectStates == null) ProjectStates = new List<ProjectNavigationState>();
            if (CollapsedProjects == null) CollapsedProjects = new List<string>();
            if (HiddenCliSessions == null) HiddenCliSessions = new List<string>();
            if (ModelProfiles == null) ModelProfiles = new List<ModelProfile>();
            if (RequestRates == null) RequestRates = new List<RequestRate>();
            if (ModelProfile.IsModelId(Model))
            {
                var old = new ModelProfile { Id = Guid.NewGuid().ToString(), Name = Model, ModelId = Model };
                ModelProfiles.Add(old);
                foreach (var conversation in Conversations)
                {
                    if (conversation.ModelProfileId == null) conversation.ModelProfileId = old.Id;
                }
            }
            Model = null;
        }
    }

    internal sealed class DataStore
    {
        private readonly string path;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };

        public readonly StatisticsStore Statistics;
        public string AttachmentDirectory { get { return Path.Combine(Path.GetDirectoryName(path), "attachments"); } }

        public DataStore(string directory)
        {
            path = Path.Combine(directory, "state.json");
            Statistics = new StatisticsStore(directory);
        }

        public AppData Read()
        {
            if (!File.Exists(path)) return new AppData();
            var data = json.Deserialize<AppData>(File.ReadAllText(path, Encoding.UTF8));
            if (data == null || data.Conversations == null) throw new IOException("对话记录格式无效。");
            data.MigrateModel();
            return data;
        }

        public void Save(AppData data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string staging = path + ".tmp";
            File.WriteAllText(staging, json.Serialize(data), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(staging, path, null);
            else File.Move(staging, path);
        }
    }

    internal static class Theme
    {
        public static readonly TimeSpan TransitionDuration = TimeSpan.FromMilliseconds(400);

        public static void Animate(ResourceDictionary resources, bool dark)
        {
            if (!SystemParameters.ClientAreaAnimation) { Apply(resources, dark); return; }
            var palette = new ResourceDictionary();
            Apply(palette, dark);
            foreach (string key in palette.Keys)
            {
                var target = (SolidColorBrush)palette[key];
                var brush = resources[key] as SolidColorBrush;
                if (brush == null || brush.IsFrozen) { resources[key] = target; continue; }
                Color current = brush.Color;
                brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                brush.Color = target.Color;
                brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(current, target.Color, TransitionDuration) {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.Stop
                }, HandoffBehavior.SnapshotAndReplace);
            }
        }

        public static void Apply(ResourceDictionary resources, bool dark)
        {
            Set(resources, "Terminal", dark ? "#0F0F12" : "#FFFFFF");
            Set(resources, "InputSurface", dark ? "#202024" : "#FFFFFF");
            Set(resources, "IconTile", dark ? "#27272A" : "#F0F0F5");
            Set(resources, "SubtleInk", dark ? "#85858F" : "#71717A");
            Set(resources, "CardHoverLine", dark ? "#45454E" : "#CAC7D4");
            Set(resources, "PrimaryAction", dark ? "#7C3AED" : "#7C3AED");
            Set(resources, "PermissionWarning", dark ? "#FB923C" : "#B54708");
            Set(resources, "Warning", dark ? "#FBBF24" : "#946000");
            Set(resources, "WarningSurface", dark ? "#332B1C" : "#FFF4D8");
            Set(resources, "ThemeTrack", dark ? "#111215" : "#E8E6E1");
            Set(resources, "ThemeKnob", dark ? "#292C3C" : "#FFF0CD");
            Set(resources, "ThemeKnobLine", dark ? "#7279AA" : "#F5D58B");
            Set(resources, "HeroSurface", dark ? "#242035" : "#F4F2FF");
            Set(resources, "HeroLine", dark ? "#333150" : "#E9E5FF");
            Set(resources, "Canvas", dark ? "#121215" : "#F7F9FD");
            Set(resources, "Sidebar", dark ? "#18181B" : "#FFFFFF");
            Set(resources, "Panel", dark ? "#18181B" : "#FFFFFF");
            Set(resources, "Composer", dark ? "#333333" : "#F1F1F3");
            Set(resources, "ComposerLine", dark ? "#454545" : "#DEDEE3");
            Set(resources, "ComposerHover", dark ? "#414141" : "#E4E4E8");
            Set(resources, "ComposerHint", dark ? "#929292" : "#797982");
            Set(resources, "SendAction", dark ? "#2F6DCE" : "#2F6DCE");
            Set(resources, "Hover", dark ? "#242428" : "#F3F5FA");
            Set(resources, "Selected", dark ? "#2A243B" : "#F4F2FF");
            Set(resources, "Line", dark ? "#29292F" : "#E7E7EC");
            Set(resources, "Ink", dark ? "#E4E4E7" : "#272534");
            Set(resources, "MutedInk", dark ? "#A1A1AA" : "#64646F");
            Set(resources, "Accent", dark ? "#B7A2FF" : "#6D28D9");
            Set(resources, "AccentInk", dark ? "#FFFFFF" : "#FFFFFF");
            Set(resources, "UserInk", dark ? "#C8BFFF" : "#624AFF");
            Set(resources, "UserSurface", dark ? "#2D2B4D" : "#F4F2FF");
            Set(resources, "AssistantInk", dark ? "#C8BFFF" : "#624AFF");
            Set(resources, "AssistantSurface", dark ? "#2D2B4D" : "#F4F2FF");
            Set(resources, "Success", dark ? "#6EE7B7" : "#18794E");
            Set(resources, "SuccessSurface", dark ? "#153329" : "#E9F8EF");
            Set(resources, "FailureSurface", dark ? "#3B2024" : "#FDEDEE");
            Set(resources, "Failure", dark ? "#FCA5A5" : "#B42332");
        }

        private static void Set(ResourceDictionary resources, string name, string value)
        {
            Color color = (Color)ColorConverter.ConvertFromString(value);
            var brush = resources[name] as SolidColorBrush;
            if (brush == null || brush.IsFrozen) resources[name] = new SolidColorBrush(color);
            else
            {
                brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                brush.Color = color;
            }
        }
    }
}
