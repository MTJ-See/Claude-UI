using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;

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

    public sealed class Conversation
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
        public string MaxTurns { get; set; }
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
        public static void Apply(ResourceDictionary resources, bool dark)
        {
            Set(resources, "Canvas", dark ? "#191919" : "#FFFFFF");
            Set(resources, "Sidebar", dark ? "#202020" : "#F7F7F5");
            Set(resources, "Panel", dark ? "#232323" : "#FFFFFF");
            Set(resources, "Composer", dark ? "#303030" : "#F4F4F3");
            Set(resources, "ComposerLine", dark ? "#3C3C3C" : "#E7E7E4");
            Set(resources, "ComposerHover", dark ? "#3D3D3D" : "#E7E7E4");
            Set(resources, "ComposerHint", dark ? "#969692" : "#83837F");
            Set(resources, "SendAction", dark ? "#3778D6" : "#2F6DCE");
            Set(resources, "Hover", dark ? "#2C2C2C" : "#ECECE9");
            Set(resources, "Selected", dark ? "#343434" : "#EEEEEB");
            Set(resources, "Line", dark ? "#363636" : "#E6E6E3");
            Set(resources, "Ink", dark ? "#F1F1EF" : "#242424");
            Set(resources, "MutedInk", dark ? "#ADADA8" : "#73736E");
            Set(resources, "Accent", dark ? "#EEEEEC" : "#242424");
            Set(resources, "AccentInk", dark ? "#202020" : "#FFFFFF");
            Set(resources, "UserInk", dark ? "#A5C9ED" : "#315C85");
            Set(resources, "UserSurface", dark ? "#253446" : "#EEF4FA");
            Set(resources, "AssistantInk", dark ? "#F0AC91" : "#A64D30");
            Set(resources, "AssistantSurface", dark ? "#3E2E28" : "#FBEFE9");
            Set(resources, "Success", dark ? "#76C8A3" : "#207653");
            Set(resources, "SuccessSurface", dark ? "#233B30" : "#EDF5EF");
            Set(resources, "FailureSurface", dark ? "#442B2A" : "#FBEFEE");
            Set(resources, "Failure", dark ? "#FF9C93" : "#A53232");
        }

        private static void Set(ResourceDictionary resources, string name, string value)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            resources[name] = brush;
        }
    }
}
