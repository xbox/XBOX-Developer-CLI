---
title: Report issue
description: The hidden compatibility alias for the feedback command.
---

# Report issue

`report-issue` is a hidden alias for [`feedback`](feedback.md). It accepts the same
options and runs the same interactive workflow, rather than only printing a URL.

```bash
xbox report-issue --no-browser
```

Use `--no-browser` to print the environment summary and issue URL without prompting
or opening a browser. Like `feedback`, it refuses `--output table`.
