using System;
using System.Globalization;

namespace Pebble
{
    /// <summary>Tiny calculator for the launcher: + - * / ^ %, parentheses, sqrt/sin/cos/tan/log/ln/abs, pi and e.</summary>
    static class Calc
    {
        public static bool LooksLikeMath(string s)
        {
            s = s.Trim();
            if (s.StartsWith("=")) return true;
            bool digit = false, op = false;
            foreach (char c in s)
            {
                if (char.IsDigit(c)) digit = true;
                else if ("+-*/^%()x×÷".IndexOf(c) >= 0) op = true;
            }
            return digit && (op || s.StartsWith("sqrt") || s.StartsWith("pi"));
        }

        public static bool TryEval(string s, out double value)
        {
            value = 0;
            try
            {
                var p = new Parser(s.Trim().TrimStart('=').Replace('×', '*').Replace('÷', '/').Replace(',', '.'));
                value = p.Expr();
                p.Ws();
                return p.Done && !double.IsNaN(value) && !double.IsInfinity(value);
            }
            catch { return false; }
        }

        public static string Format(double v)
        {
            if (Math.Abs(v) >= 1e15 || (Math.Abs(v) < 1e-6 && v != 0)) return v.ToString("G10", CultureInfo.CurrentCulture);
            return Math.Round(v, 10).ToString("#,0.##########", CultureInfo.CurrentCulture);
        }

        sealed class Parser
        {
            readonly string _s; int _i;
            public Parser(string s) { _s = s.ToLowerInvariant(); }
            public bool Done => _i >= _s.Length;
            public void Ws() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }
            bool Eat(char c) { Ws(); if (_i < _s.Length && _s[_i] == c) { _i++; return true; } return false; }

            public double Expr()
            {
                double v = Term();
                while (true)
                {
                    if (Eat('+')) v += Term();
                    else if (Eat('-')) v -= Term();
                    else return v;
                }
            }

            double Term()
            {
                double v = Power();
                while (true)
                {
                    if (Eat('*') || Eat('x')) v *= Power();
                    else if (Eat('/')) v /= Power();
                    else if (Eat('%')) { Ws(); if (Done || _s[_i] == ')' || "+-".IndexOf(_s[_i]) >= 0) v /= 100; else v %= Power(); }
                    else return v;
                }
            }

            double Power()
            {
                double b = Unary();
                if (Eat('^')) return Math.Pow(b, Power());
                return b;
            }

            double Unary()
            {
                if (Eat('-')) return -Unary();
                if (Eat('+')) return Unary();
                return Atom();
            }

            double Atom()
            {
                Ws();
                if (Eat('(')) { double v = Expr(); Eat(')'); return v; }
                int start = _i;
                if (_i < _s.Length && char.IsLetter(_s[_i]))
                {
                    while (_i < _s.Length && char.IsLetter(_s[_i])) _i++;
                    string name = _s.Substring(start, _i - start);
                    switch (name)
                    {
                        case "pi": return Math.PI;
                        case "e": return Math.E;
                    }
                    double a = Atom();
                    switch (name)
                    {
                        case "sqrt": return Math.Sqrt(a);
                        case "sin": return Math.Sin(a * Math.PI / 180);
                        case "cos": return Math.Cos(a * Math.PI / 180);
                        case "tan": return Math.Tan(a * Math.PI / 180);
                        case "log": return Math.Log10(a);
                        case "ln": return Math.Log(a);
                        case "abs": return Math.Abs(a);
                        default: throw new FormatException(name);
                    }
                }
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                if (start == _i) throw new FormatException();
                return double.Parse(_s.Substring(start, _i - start), CultureInfo.InvariantCulture);
            }
        }
    }
}
