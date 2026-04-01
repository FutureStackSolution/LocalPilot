<div align="center">
  <img src="LocalPilot/Assets/Logo_Concept_Minimalist.png" height="128" />
  <h1>⚡ LocalPilot</h1>
  <p><strong>The Privacy-First AI Pair Programmer for Visual Studio.</strong></p>

  [![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
  [![Ollama Support](https://img.shields.io/badge/Ollama-Compatible-orange.svg)](https://ollama.com)
  [![Visual Studio](https://img.shields.io/badge/Visual_Studio-2022-purple.svg)](https://visualstudio.microsoft.com/)
  [![Visual Studio Marketplace](https://img.shields.io/visual-studio-marketplace/v/FutureStackSolution.LocalPilot?color=blue&label=VS%20Marketplace)](https://marketplace.visualstudio.com/items?itemName=FutureStackSolution.LocalPilot)
</div>

---

## 🌟 Overview

**LocalPilot** is a powerful, privacy-first Visual Studio extension that brings the power of local LLMs directly into your coding environment through [Ollama](https://ollama.com). No cloud dependencies, no monthly subscriptions, and absolute privacy—your code never leaves your machine.

### 🖼️ Showcase
*(Example UI of LocalPilot in action)*
<div align="center">
  <img src="LocalPilot/Assets/Showcase_Mockup.png" width="90%" alt="LocalPilot Showcase" />
</div>

---

## 🚀 Key Features

*   ✨ **Ghost-Text Inline Completions**: Predictive code suggestions provided in real-time as you type, perfectly integrated into the editor.
*   💬 **Advanced Chat Interface**: A full-featured chat panel for reasoning, generation, and complex coding discussions.
<div align="center">
  <img src="LocalPilot/Assets/ChatInterface.png" width="90%" alt="Chat Interface" />
</div>
*   ⚡ **Quick Actions**: Instant context-menu actions to Explain, Refactor, Document, or Fix code snippets with a single click.

<div align="center">
  <img src="LocalPilot/Assets/QuickActions.png" width="90%" alt="Quick Action" />
</div>
*   🛠️ **Customizable Architectures**: Scale your experience by choosing different backend models for different tasks (e.g., a lightning-fast model for inline suggestions and a reasoning-heavy model for chat).
<div align="center">
  <img src="LocalPilot/Assets/LocalPilotConfiguration.png" width="90%" alt="Local Pilot Configuration" />
</div>



---

## 🛡️ Why Choose LocalPilot?

-   **Total Privacy**: For enterprise-level security, your source code remains strictly local. No telemetry, no third-party data collection.
-   **No Latency**: By running inference on your own hardware, you bypass network lag and cloud service downtime.
-   **Cost Efficiency**: Leverage your own machine's power without the burden of recurring token costs or enterprise AI service fees.
-   **Seamless Integration**: Designed as a first-class citizen of Visual Studio, matching the theme and workflow you already know.

---

## 🛠️ Getting Started

### 1. Prerequisites
You must have **Ollama** installed and running on your device.
*   **Download**: [ollama.com](https://ollama.com)
*   **Launch a Model**: We recommend running models optimized for code, such as `llama3`, `codellama`, or `phi3`.
    ```bash
    ollama run llama3
    ```

### 2. Installation
1. Go to the [Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=FutureStackSolution.LocalPilot).
2. Click **Download** or search for "LocalPilot" directly within Visual Studio:
   - **Extensions** > **Manage Extensions** > **Online**.
3. Install the extension and restart Visual Studio.

### 3. Connection Setup
Navigate to **Tools** > **Options** > **LocalPilot** > **Settings**.
-   **Ollama Base URL**: Default is `http://localhost:11434`. Click **"Test Connection"** to verify the link.
-   **Model Assignments**: Assign your preferred models for **Chat** and **Inline Completions**.
    > [!TIP]
    > For the best experience, use a smaller/faster model (like `phi3` or `starcoder2:3b`) for **Inline Completions** and a larger model (like `llama3:8b` or `deepseek-coder`) for the **Chat Assistant**.

---

## 📖 Usage Guide

### 💡 Inline Completion
Simply type in any code file. LocalPilot will display a translucent suggestion.
*   **[Tab]**: Accept the suggestion.
*   **[Esc]**: Dismiss.

### ⚡ Smart Context Menu
Highlight any code, right-click, and select **LocalPilot** to:
-   **Explain Code**: Understand complex logic instantly.
-   **Generate Documentation**: Automatically create XML/docstring comments.
-   **Refactor selection**: Improve code quality and readability.

---

### 🤝 How to Contribute

We love community involvement! Whether you're fixing a bug, suggesting a feature, or helping with documentation, your input is welcome.

#### 🛠️ Reporting Issues
If you encounter any problems or have a great idea for a new feature, please follow these steps to create an issue:
1.  **Search First**: Check the [Existing Issues](https://github.com/FutureStackSolution/LocalPilot/issues) to see if someone else has already reported the problem.
2.  **Create a Clear Title**: Be concise but descriptive. (e.g., *"Bug: Inline completions don't show up in C# files"*).
3.  **Provide Context**:
    - Your **Visual Studio version** (e.g., VS 2022 v17.10).
    - Your **Ollama model** and version.
    - Steps to reproduce the issue.
4.  **Add Labels**: Use labels like `bug`, `enhancement`, or `question` to help us organize the workflow.

#### 💻 Pull Requests
1. Fork the repository and create your branch from `main`.
2. Ensure your code builds without errors or warnings.
3. Submit a Pull Request with a detailed description of your changes.




