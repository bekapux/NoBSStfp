using System;
using System.Collections.Generic;
using System.Text;

namespace NoBSSftp.Services;

public class TerminalEmulator
{
    private readonly int _rows;
    private readonly int _cols;
    private readonly char[][] _screen;
    private int _cursorX;
    private int _cursorY;
    private readonly List<string> _history = [];
    private bool _isAlternateScreen;

    public TerminalEmulator(int rows, int cols)
    {
        _rows = rows;
        _cols = cols;
        _screen = new char[_rows][];
        for (int i = 0; i < _rows; i++)
        {
            _screen[i] = new char[_cols];
            Array.Fill(_screen[i], ' ');
        }
    }

    public void Write(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\x1b')
            {
                if (i + 1 < text.Length && text[i + 1] == '[')
                {
                    int j = i + 2;
                    while (j < text.Length && (char.IsDigit(text[j]) || text[j] == ';' || text[j] == '?' || text[j] == '>')) j++;
                    if (j < text.Length)
                    {
                        char cmd = text[j];
                        string param = text.Substring(i + 2, j - (i + 2));
                        HandleCsi(cmd, param);
                        i = j + 1;
                        continue;
                    }
                }
                else if (i + 1 < text.Length && text[i + 1] == '(') // G0 Character Set
                {
                    i += 3;
                    continue;
                }
                else if (i + 1 < text.Length && text[i + 1] == ']') // OSC
                {
                    int j = i + 2;
                    while (j < text.Length && text[j] != '\x07' && text[j] != '\x1b') j++;
                    if (j < text.Length && text[j] == '\x1b' && j + 1 < text.Length && text[j + 1] == '\\') j++;
                    i = j + 1;
                    continue;
                }
                else if (i + 1 < text.Length && (text[i + 1] == '=' || text[i + 1] == '>')) // Keypad modes
                {
                    i += 2;
                    continue;
                }
            }

            if (c == '\n')
            {
                _cursorY++;
                if (_cursorY >= _rows) Scroll();
            }
            else if (c == '\r')
            {
                _cursorX = 0;
            }
            else if (c == '\f')
            {
                for (int j = 0; j < _rows; j++) Array.Fill(_screen[j], ' ');
                _cursorX = 0;
                _cursorY = 0;
            }
            else if (c == '\b')
            {
                if (_cursorX > 0) _cursorX--;
            }
            else if (c == '\t')
            {
                _cursorX = (_cursorX / 8 + 1) * 8;
                if (_cursorX >= _cols)
                {
                    _cursorX = 0;
                    _cursorY++;
                    if (_cursorY >= _rows) Scroll();
                }
            }
            else if (c >= 32)
            {
                if (_cursorX < _cols && _cursorY < _rows)
                {
                    _screen[_cursorY][_cursorX] = c;
                    _cursorX++;
                    if (_cursorX >= _cols)
                    {
                        _cursorX = 0;
                        _cursorY++;
                        if (_cursorY >= _rows) Scroll();
                    }
                }
            }
            i++;
        }
    }

    private void HandleCsi(char cmd, string param)
    {
        switch (cmd)
        {
            case 'H':
            case 'f':
                {
                    int r = 1, c = 1;
                    var parts = param.Split(';');
                    if (parts.Length >= 1 && int.TryParse(parts[0], out int pr)) r = pr;
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int pc)) c = pc;
                    _cursorY = Math.Clamp(r - 1, 0, _rows - 1);
                    _cursorX = Math.Clamp(c - 1, 0, _cols - 1);
                }
                break;
            case 'A': // Cursor Up
                {
                    int n = string.IsNullOrEmpty(param) ? 1 : int.Parse(param);
                    _cursorY = Math.Max(0, _cursorY - n);
                }
                break;
            case 'B': // Cursor Down
                {
                    int n = string.IsNullOrEmpty(param) ? 1 : int.Parse(param);
                    _cursorY = Math.Min(_rows - 1, _cursorY + n);
                }
                break;
            case 'C': // Cursor Forward
                {
                    int n = string.IsNullOrEmpty(param) ? 1 : int.Parse(param);
                    _cursorX = Math.Min(_cols - 1, _cursorX + n);
                }
                break;
            case 'D': // Cursor Backward
                {
                    int n = string.IsNullOrEmpty(param) ? 1 : int.Parse(param);
                    _cursorX = Math.Max(0, _cursorX - n);
                }
                break;
            case 'J':
                if (param == "2" || param == "?2" || string.IsNullOrEmpty(param))
                {
                    for (int i = 0; i < _rows; i++) Array.Fill(_screen[i], ' ');
                    _cursorX = 0; _cursorY = 0;
                }
                else if (param == "0")
                {
                    Array.Fill(_screen[_cursorY], ' ', _cursorX, _cols - _cursorX);
                    for (int i = _cursorY + 1; i < _rows; i++) Array.Fill(_screen[i], ' ');
                }
                else if (param == "1")
                {
                    for (int i = 0; i < _cursorY; i++) Array.Fill(_screen[i], ' ');
                    Array.Fill(_screen[_cursorY], ' ', 0, _cursorX + 1);
                }
                break;
            case 'K': // Erase in line
                if (param == "0" || string.IsNullOrEmpty(param))
                    Array.Fill(_screen[_cursorY], ' ', _cursorX, _cols - _cursorX);
                else if (param == "1")
                    Array.Fill(_screen[_cursorY], ' ', 0, Math.Min(_cursorX + 1, _cols));
                else if (param == "2")
                    Array.Fill(_screen[_cursorY], ' ');
                break;
            case 'h':
                if (param == "?1049") _isAlternateScreen = true;
                break;
            case 'l':
                if (param == "?1049") _isAlternateScreen = false;
                break;
            case 'm': 
                break;
        }
    }

    private void Scroll()
    {
        if (!_isAlternateScreen)
        {
            _history.Add(new string(_screen[0]).TrimEnd());
            if (_history.Count > 1000) _history.RemoveAt(0);
        }

        for (var i = 0; i < _rows - 1; i++) Array.Copy(_screen[i + 1], _screen[i], _cols);
        Array.Fill(_screen[_rows - 1], ' ');
        _cursorY = _rows - 1;
    }

    public string GetFullText()
    {
        var sb = new StringBuilder();
        if (!_isAlternateScreen)
        {
            foreach (var line in _history) sb.AppendLine(line);
        }
        
        for (var i = 0; i < _rows; i++)
        {
            var line = new string(_screen[i]).TrimEnd();
            if (i < _rows - 1 || line.Length > 0) sb.AppendLine(line);
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }
}
