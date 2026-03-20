# ⚡ LocalPilot

**LocalPilot** is a powerful, privacy-first Visual Studio extension that brings the power of local LLMs (via [Ollama](https://ollama.com)) directly into your coding environment. No cloud, no subscription, no tracking—just fast, local AI.

---

## 🚀 Key Features

*   ✨ **Inline Ghost-Text**: Real-time code suggestions as you type.
*   💬 **Advanced Chat Assistant**: A dedicated panel for full-code generation and reasoning.
*   ⚡ **Quick Actions**: One-click actions to Explain, Refactor, Document, and Fix code.
*   🛠️ **Customizable**: Choose different backend models for different tasks (e.g., a fast model for inline, a smart model for chat).

---

## 🛠️ Configuration Tutorial

### 1. Prerequisites
Ensure you have **Ollama** installed and running on your local machine.
*   Download it from [ollama.com](https://ollama.com).
*   Run your preferred models (e.g. `ollama run llama3`, `ollama run codellama`).

### 2. Connect to LocalPilot
In Visual Studio, go to **Tools** > **Options** > **LocalPilot** > **Settings**.
*   **Ollama Base URL**: Default is `http://localhost:11434`. Click "Test Connection" to verify.
*   **Model Configuration**: Choose which models to use for each feature.
    *   *Tip*: Use a smaller, faster model (like `phi3` or `codegemma:2b`) for **Inline Completion** and a larger model (like `llama3` or `codellama`) for **Chat**.

### 3. Advanced Settings
Feeling like a power-user? Find the **🛠️ Advanced Generation Settings** at the bottom:
*   **Temperature**: Lower values (like `0.2`) make the code more stable and logical. Higher values add creativity.
*   **Max Tokens**: Limit how long the AI responses can be.
*   **Context Window**: Fine-tune how many lines of code the AI "sees" above and below your cursor.

---

## 📖 Usage Guide

### 💡 Inline Completion (Ghost-Text)
Simply start typing in any C# or supported code file. **LocalPilot** will show a translucent "ghost-text" preview of the suggested code.
*   **Tab**: Accept the suggestion.
*   **Esc**: Ignore.

### 💬 Chat Assistant
Open the chat by going to **View** > **Other Windows** > **LocalPilot Chat** (or use the shortcut in the Tools menu).
*   Type your question and press **Enter**.
*   **⚡ Quick Actions**: Use the "Quick Actions" button at the bottom for instant documentation or refactoring of your current selection.

### ⚡ Context Menu
Right-click any code block in the editor and find the **⚡ LocalPilot** menu:
*   **Explain Code**: Get a breakdown of what the code does.
*   **Generate Documentation**: Automatically insert XML comments.
*   **Refactor Code**: Get an optimized version of the selected snippet.

---

## 🐛 Reporting Issues & Guidelines

We value your feedback! If you find a bug or have a feature request, please follow these steps:

### How to Report an Issue
1.  **Check for Duplicates**: Search the current issues to see if someone else has already reported it.
2.  **Use the Template**: When opening a new issue, please include:
    *   **VS Version**: (e.g., Visual Studio 2022 v17.9)
    *   **Ollama Model**: (e.g., Llama-3 8B)
    *   **Steps to Reproduce**: Detailed steps on how to trigger the bug.
    *   **Expected vs. Actual Behavior**: What happened and what should have happened.
    *   **Screenshots/Logs**: Any visual proof or error messages from the Output window.

### Issue Guidelines
*   **Be Specific**: "It doesn't work" doesn't help. "The chat panel hangs when I select more than 1000 lines" is much better.
*   **Respect the Scope**: We focus on local LLM integration. Features requiring external cloud APIs are currently out of scope.
*   **Be Kind**: We are a community of developers helping each other!

---

*Powered by [Ollama](https://ollama.com)*
