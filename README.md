# AZ-900 Question Processor

A .NET application that processes AZ-900 exam questions using AI, stores them in SQLite, and syncs them to Anki for study.

## Features

- **AI Processing**: Automatically processes raw exam questions and extracts structured information (question, options, correct answer, explanations, etc.)
- **SQLite Storage**: Persistent storage of all questions and AI-generated content
- **Anki Integration**: Sync processed questions to Anki via AnkiConnect
- **Skip Existing**: Automatically skips questions that already have AI results in the database
- **JSON Export/Import**: Optional JSON export for backup or manual import

## Requirements

- .NET 10.0 SDK
- Anki with AnkiConnect plugin (for Anki sync mode)
- AI model endpoint (default: LM Studio at `http://localhost:1234/v1`)

## Quick Start

```bash
# Build the project
dotnet build

# Run in AI mode (process questions, save to DB)
dotnet run -- ai --db az900.db --limit 20

# Export results to JSON (optional)
dotnet run -- ai --out outdir --db az900.db --limit 20

# Sync to Anki (loads from DB by default)
dotnet run -- anki --db az900.db --deck az-900

# Or load from JSON file
dotnet run -- anki --in questions.json --deck az-900

# Run both modes sequentially
dotnet run -- all --out outdir --db az900.db --limit 20 --deck az-900

# Show help
dotnet run -- help
```

## Usage

### AI Mode

Processes questions from `data/1.txt` and `data/2.txt` (default input files), sends them to AI for processing, and saves results to the database.

```bash
dotnet run -- ai [--out <folder-or-file>] [--db <path>] [--limit <n>]
```

**Options:**
- `--out` / `-o`: Optional JSON export path (folder or file). If not specified, results are saved to DB only.
- `--db`: Database path (default: `az900.db`)
- `--limit`: Maximum number of questions to process (optional)

**Behavior:**
- Automatically skips questions that already have AI results in the database
- Saves each question immediately after processing
- Logs progress and skipped questions

### Anki Mode

Syncs processed questions from the database (or JSON file) to Anki via AnkiConnect.

```bash
dotnet run -- anki [--in <file>] [--db <path>] [--deck <name>] [--model <name>]
```

**Options:**
- `--in` / `-i`: Optional JSON file to load from. If not specified, loads from database.
- `--db`: Database path (default: `az900.db`)
- `--deck`: Anki deck name (default: `az-900`)
- `--model`: Anki note type/model (default: `Basic`)

**Requirements:**
- Anki must be running with AnkiConnect plugin installed
- AnkiConnect must be accessible at `http://127.0.0.1:8765`

### All Mode

Runs AI mode followed by Anki mode sequentially.

```bash
dotnet run -- all [--in <file>] [--out <folder-or-file>] [--db <path>] [--limit <n>] [--deck <name>] [--model <name>]
```

## Defaults

- **Database**: `az900.db`
- **Deck**: `az-900`
- **Model**: `Basic`
- **Input files**: `data/1.txt`, `data/2.txt`
- **JSON export**: Optional (disabled by default)
- **AnkiConnect URL**: `http://127.0.0.1:8765`

## Project Structure

All code is currently in `Program.cs`:
- CLI entry point and argument parsing
- Question models (`Question`, `AIParsed`)
- AI provider abstraction and Semantic Kernel integration (`SK`, `IAIProvider`)
- AnkiConnect integration and HTML formatting utilities (`Anki` class)
- SQLite repository (`QuestionRepository`) for data persistence

## Database Schema

Questions are stored in SQLite with the following structure:

- `id`: Primary key
- `header`: Unique question identifier
- `raw`: Raw question text
- `airaw`: Raw AI response
- `aiquestion`, `aioptions`, `aicorrectanswer`, etc.: Parsed AI fields
- `created_at`, `updated_at`: Timestamps

## Configuration

### AI Model

The application uses Semantic Kernel to connect to AI models. Default configuration:
- Endpoint: `http://localhost:1234/v1` (LM Studio)
- Model: `qwen3-coder-30b-a3b-instruct-mlx`

To change AI settings, modify the `SK` class in `Program.cs`.

### Input Files

By default, questions are loaded from:
- `data/1.txt`
- `data/2.txt`

Questions should start with a line matching: `Topic \d+ Question #\d+`

## Error Handling

- Failed Anki sync attempts save results to `az900-anki-failed.json` for manual import
- AI processing errors are logged but don't stop the entire batch
- Database errors are logged with context

## Development

### Code Style

- 4-space indentation
- PascalCase for classes, methods, properties, constants
- camelCase for variables and parameters
- Nullable reference types enabled
- Async/await patterns for all I/O operations
- Use `record` for immutable data models
- Use `class` for service types with logic

### Dependencies

- Microsoft.SemanticKernel
- Microsoft.SemanticKernel.Connectors.OpenAI
- Microsoft.Data.Sqlite
- System.Text.Json

