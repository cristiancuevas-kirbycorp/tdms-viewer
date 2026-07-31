using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TdmsViewer.Models;

namespace TdmsViewer.Services;

public interface IProjectService
{
    void Save(ProjectModel project, string path);
    ProjectModel Load(string path);
}

public sealed class ProjectService : IProjectService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public void Save(ProjectModel project, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(project, Options));

    public ProjectModel Load(string path) =>
        JsonSerializer.Deserialize<ProjectModel>(File.ReadAllText(path), Options)
        ?? new ProjectModel();
}
