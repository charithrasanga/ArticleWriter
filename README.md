# AI Article Writer

A .NET 8 console application that uses a **multi-agent pipeline** powered by **Azure OpenAI** to research, write, quality-check, and publish beautifully styled HTML articles — automatically, end-to-end.

---

## Features

- **Multi-agent pipeline** — four specialised AI agents collaborate in sequence: Research → Content Creation → Quality Assurance → Presentation
- **Live web research** — the Research agent searches the web via [Serper](https://serper.dev) and validates every URL before citing it
- **Quality gate** — a dedicated QA agent scores the draft and triggers revision cycles until it meets the configurable quality threshold
- **Language-aware output** — write articles in any language; all structural labels (headings, TOC, "Key Takeaways", etc.) are automatically localised
- **Responsive HTML output** — articles are rendered as fully responsive, beautifully styled HTML pages with hero images, a sticky table-of-contents sidebar, reading progress bar, and section images from Unsplash
- **Rich console UX** — real-time progress display with [Spectre.Console](https://spectreconsole.net/), tool-call logs, and agent conversation viewer
- **Configurable** — audience, tone, article length, quality threshold, and revision limits are all configurable

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                     ArticleWorkflowOrchestrator                 │
│                                                                 │
│  ┌──────────────┐    ┌───────────────────┐    ┌─────────────┐  │
│  │ ResearchAgent │───▶│ContentCreation    │───▶│   Quality   │  │
│  │              │    │Agent              │    │Assurance    │  │
│  │ • Web search │    │                   │    │Agent        │  │
│  │ • URL check  │    │ • Writes article  │    │             │  │
│  │ • Fact gather│    │ • Section check   │    │ • Scoring   │  │
│  └──────────────┘    │ • Revisions       │    │ • Feedback  │  │
│                      └───────────────────┘    └──────┬──────┘  │
│                                                       │         │
│                              ┌────────────────────────▼──────┐  │
│                              │      PresentationAgent         │  │
│                              │                                │  │
│                              │  • HTML rendering (C#)         │  │
│                              │  • Unsplash image resolution   │  │
│                              │  • TOC, progress bar, sidebar  │  │
│                              └────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

### Agent Roles

| Agent | Responsibility | Tools Used |
|---|---|---|
| **ResearchAgent** | Web research and source validation | Serper web search, URL validator |
| **ContentCreationAgent** | Draft and revise the article JSON | Section count checker |
| **QualityAssuranceAgent** | Score quality and identify gaps | Word counter |
| **PresentationAgent** | Render the final HTML document | Unsplash image API |

### Tool Infrastructure

All agents extend `BaseAgent` which provides:
- **Manual tool-call loop** — up to 10 iterations of model → tool → model
- **Retry with exponential back-off** — handles HTTP 429 / 5xx transient errors
- **Console tool-call reporter** — logs every tool invocation and result to the terminal in real time

---

## Project Structure

```
ArticleWriter/
├── Agents/
│   ├── BaseAgent.cs                  # Retry, tool-call loop, reporter wiring
│   ├── ResearchAgent.cs              # Web research + source validation
│   ├── ContentCreationAgent.cs       # Draft & revision
│   ├── QualityAssuranceAgent.cs      # QA scoring
│   ├── PresentationAgent.cs          # HTML rendering
│   ├── IAgents.cs                    # Agent interfaces
│   └── IToolCallReporter.cs          # Tool-log abstraction
├── ArticleWriter.Core/
│   └── Agents/
│       └── ArticleWriterWorkflow.cs  # Orchestrator (pipeline coordination)
├── Models/
│   ├── ArticleModels.cs              # Article / config / image models
│   ├── AgentRequest.cs               # Request/response shapes
│   ├── QualityModels.cs              # QA assessment models
│   └── ResearchModels.cs             # Research result models
├── Presentation/
│   ├── ConsoleUI.cs                  # Spectre.Console interactive UI
│   └── ConsoleToolCallReporter.cs    # Real-time tool-call console output
├── Services/
│   └── UnsplashService.cs            # Unsplash image resolution
├── Tools/
│   ├── WebSearchTool.cs              # Serper Google Search wrapper
│   ├── UrlValidatorTool.cs           # Reachability checker AI function
│   └── ArticleCheckTool.cs           # Word count + section count tools
├── Utils/
│   └── UrlValidator.cs               # HTTP HEAD validator
├── appsettings.json                  # Configuration template (no secrets)
├── appsettings.Development.json      # Local secrets — NOT committed to git
└── Program.cs                        # DI wiring + host setup
```

---

## Prerequisites

| Requirement | Version |
|---|---|
| .NET SDK | 8.0 or later |
| Azure OpenAI resource | GPT-4 deployment |
| Serper API key | [serper.dev](https://serper.dev) (free tier available) |
| Unsplash API key | [unsplash.com/developers](https://unsplash.com/developers) (free tier available) |

---

## Getting Started

### 1. Clone the repository

```bash
git clone https://github.com/charithrasanga/ArticleWriter.git
cd ArticleWriter
```

### 2. Configure credentials

Copy the template and fill in your keys:

```bash
cp appsettings.json appsettings.Development.json
```

Edit `appsettings.Development.json`:

```json
{
  "AzureOpenAI": {
    "Endpoint": "https://<your-resource>.openai.azure.com/",
    "ApiKey": "<your-azure-openai-api-key>",
    "DeploymentName": "gpt-4"
  },
  "Images": {
    "AccessKey": "<your-unsplash-access-key>"
  },
  "Serper": {
    "ApiKey": "<your-serper-api-key>"
  }
}
```

> **Note:** `appsettings.Development.json` is listed in `.gitignore` and will never be committed.

#### Passwordless authentication (optional)

Leave `AzureOpenAI.ApiKey` empty to use `DefaultAzureCredential` (Managed Identity / Azure CLI login):

```json
"AzureOpenAI": {
  "Endpoint": "https://<your-resource>.openai.azure.com/",
  "ApiKey": "",
  "DeploymentName": "gpt-4"
}
```

### 3. Run

```bash
dotnet run
```

The interactive console will prompt you for:
- **Topic** — what the article is about (any language)
- **Target audience** — choose from the configured list
- **Tone of voice** — choose from the configured list
- **Article length** — Short / Medium / Long

The pipeline runs automatically and saves the finished article as `article_<timestamp>.html` in the project directory.

---

## Configuration Reference

All settings live in `appsettings.json` (template). Override in `appsettings.Development.json` for local development.

| Key | Description | Required |
|---|---|---|
| `AzureOpenAI:Endpoint` | Azure OpenAI resource endpoint URL | ✅ |
| `AzureOpenAI:ApiKey` | API key (leave empty for `DefaultAzureCredential`) | ⚠️ |
| `AzureOpenAI:DeploymentName` | Model deployment name | ✅ |
| `Images:AccessKey` | Unsplash API access key | ✅ |
| `Images:HeaderSize` | Header image resolution (default `1200x500`) | |
| `Images:SectionSize` | Section image resolution (default `800x450`) | |
| `Serper:ApiKey` | Serper Google Search API key | ✅ |
| `Serper:ResultCount` | Number of search results per query (default `10`) | |
| `ArticleGeneration:QualityThreshold` | Minimum QA score (0–100) before publishing (default `85`) | |
| `ArticleGeneration:MaxRevisions` | Maximum revision cycles (default `3`) | |
| `ArticleGeneration:AudienceChoices` | List of audience options shown in the UI | |
| `ArticleGeneration:ToneChoices` | List of tone options shown in the UI | |

---

## How It Works

1. **Research phase** — The `ResearchAgent` runs several Serper web searches based on the topic and key points. Every URL it finds is validated (HTTP HEAD check) before being included in the research summary passed to the content agent.

2. **Content creation** — The `ContentCreationAgent` receives the research data and writes a structured JSON article with `title`, `abstract`, `tableOfContents`, `sections[]`, `keyTakeaways`, `conclusion`, `sources`, and `labels` (localised UI strings). It uses the `CheckSectionCount` tool to self-verify that every key point has a section before finishing.

3. **Quality assurance** — The `QualityAssuranceAgent` scores the draft (0–100) across accuracy, clarity, coherence, engagement, grammar, and completeness. If the score is below the configured threshold, it produces structured revision suggestions.

4. **Revision loop** — The `ContentCreationAgent` re-runs with the QA feedback. This repeats up to `MaxRevisions` times or until the threshold is met.

5. **Presentation** — The `PresentationAgent` renders the final JSON as a fully self-contained HTML page. All structural labels (headings, TOC, "Key Takeaways", etc.) come from the `labels` field the model produced — so the UI chrome matches the article language.

---

## Output

Each run saves a file named `article_<yyyyMMdd_HHmmss>.html` to the project directory. Open it in any browser — no server required.

### Output features
- Full-bleed hero image with gradient overlay
- Sticky sticky-nav bar with article title (appears on scroll)
- Reading progress bar
- Responsive two-column layout (article + TOC sidebar)
- Per-section images from Unsplash
- Blockquote highlights per section
- Key Takeaways card
- Conclusion card
- References with clickable links
- "Generated by AI Article Writer" meta card
- Fully localised UI labels

---

## License

MIT
