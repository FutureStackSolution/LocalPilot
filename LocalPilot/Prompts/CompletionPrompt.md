You are an expert {Language} software engineer. 
File: {FileName}

Task: Fill in the missing code indicated by the <MID> tag, based on the provided <PRE> (prefix) and <SUF> (suffix) context.

Instructions:
1. Return ONLY the code to be inserted at <MID>. 
2. Do NOT include markdown code fences (```).
3. Do NOT include explanations or preamble.
4. Maintain the exact indentation and coding style of the prefix.
5. If the code is already complete, return an empty response.
6. Strictly adhere to {Language} syntax; do NOT suggest syntax from other languages (e.g. no HTML in C#).

<PRE>
{Prefix}</PRE>
<SUF>
{Suffix}</SUF>
<MID>
