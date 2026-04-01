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
*   ⚡ **Quick Actions**: Instant context-menu actions to Explain, Refactor, Document, or Fix code snippets with a single click.
*   🛠️ **Customizable Architectures**: Scale your experience by choosing different backend models for different tasks (e.g., a lightning-fast model for inline suggestions and a reasoning-heavy model for chat).

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

### 💬 Chat Assistant
Open the chat view via **View** > **Other Windows** > **LocalPilot Chat**.
*   Query the model about your selection or start a wide-ranging coding discussion.
*   Use the **Quick Actions** button below the chat box for automated task handling.

### ⚡ Smart Context Menu
Highlight any code, right-click, and select **LocalPilot** to:
-   **Explain Code**: Understand complex logic instantly.
-   **Generate Documentation**: Automatically create XML/docstring comments.
-   **Refactor selection**: Improve code quality and readability.

---

## 🐝 Contributions & Support

We welcome contributions from the community! If you encounter any bugs or have ideas for premium features, please check our [Issue Guidelines](https://github.com/FutureStackSolution/LocalPilot/issues).

### Enterprise Support
For teams looking to deploy LocalPilot at scale or needing custom internal model integrations, please contact the development team via the GitHub repository.

---

<div align="center">
  <em>Powered by [Ollama] — Built for Developers, by Developers.</em>
</div>
