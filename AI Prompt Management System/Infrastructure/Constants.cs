namespace AI_Prompt_Management_System.Infrastructure;

public static class PromptCategories
{
    public static readonly List<string> All = new()
    {
        "Code Generation",
        "Bug Fixing",
        "Testing",
        "Documentation",
        "UI Design",
        "Business Analysis",
        "Learning"
    };
}

public static class AiTools
{
    public static readonly List<string> All = new()
    {
        "ChatGPT",
        "Copilot",
        "Claude",
        "Gemini"
    };
}
