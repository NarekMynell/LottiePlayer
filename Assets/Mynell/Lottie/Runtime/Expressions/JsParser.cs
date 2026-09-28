using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mynell.Lottie.Expressions
{
    // ====================================================================== tokens

    enum Tok : byte { End, Num, Str, Ident, Punct }

    struct Token
    {
        public Tok Type;
        public string Text;
        public double Num;
        public int Pos;
        public bool NewlineBefore;
    }

    /// <summary>Tokenizer for the ECMAScript subset used by After Effects / Bodymovin expressions.</summary>
    sealed class JsLexer
    {
        readonly string _s;
        int _i;
        public readonly List<Token> Tokens = new List<Token>();

        static readonly string[] Puncts =
        {
            ">>>=", "===", "!==", ">>>", "<<=", ">>=", "**=", "...",
            "==", "!=", "<=", ">=", "&&", "||", "++", "--", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<", ">>", "=>", "**", "??",
            "{", "}", "(", ")", "[", "]", ";", ",", "<", ">", "+", "-", "*", "/", "%", "&", "|", "^", "!", "~", "?", ":", "=", ".",
        };

        public JsLexer(string source)
        {
            _s = source ?? "";
            Run();
        }

        void Run()
        {
            bool newline = false;
            while (true)
            {
                // whitespace & comments
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == '\n' || c == '\r' || c == '\u2028' || c == '\u2029') { newline = true; _i++; }
                    else if (char.IsWhiteSpace(c) || c == '\uFEFF') _i++;
                    else if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '/')
                    {
                        while (_i < _s.Length && _s[_i] != '\n') _i++;
                    }
                    else if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '*')
                    {
                        int end = _s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                        if (end < 0) end = _s.Length - 2;
                        if (_s.IndexOf('\n', _i, end - _i) >= 0) newline = true;
                        _i = end + 2;
                    }
                    else break;
                }
                if (_i >= _s.Length)
                {
                    Tokens.Add(new Token { Type = Tok.End, Pos = _i, NewlineBefore = true });
                    return;
                }
                int start = _i;
                char ch = _s[_i];
                var t = new Token { Pos = start, NewlineBefore = newline };
                newline = false;
                if (char.IsDigit(ch) || (ch == '.' && _i + 1 < _s.Length && char.IsDigit(_s[_i + 1])))
                {
                    t.Type = Tok.Num;
                    t.Num = ReadNumber();
                }
                else if (ch == '"' || ch == '\'' || ch == '`')
                {
                    t.Type = Tok.Str;
                    t.Text = ReadString(ch);
                }
                else if (IsIdentStart(ch))
                {
                    while (_i < _s.Length && IsIdentPart(_s[_i])) _i++;
                    t.Type = Tok.Ident;
                    t.Text = _s.Substring(start, _i - start);
                }
                else
                {
                    string p = null;
                    foreach (var cand in Puncts)
                    {
                        if (string.CompareOrdinal(_s, _i, cand, 0, cand.Length) == 0) { p = cand; break; }
                    }
                    if (p == null) throw new JsSyntaxError($"Unexpected character '{ch}'", start);
                    _i += p.Length;
                    t.Type = Tok.Punct;
                    t.Text = p;
                }
                Tokens.Add(t);
            }
        }

        static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == '$';
        static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

        double ReadNumber()
        {
            int start = _i;
            if (_s[_i] == '0' && _i + 1 < _s.Length && (_s[_i + 1] == 'x' || _s[_i + 1] == 'X'))
            {
                _i += 2;
                int hs = _i;
                while (_i < _s.Length && Uri.IsHexDigit(_s[_i])) _i++;
                return (double)ulong.Parse(_s.Substring(hs, _i - hs), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            if (_i < _s.Length && _s[_i] == '.')
            {
                _i++;
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            }
            if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
            {
                int save = _i;
                _i++;
                if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                if (_i < _s.Length && char.IsDigit(_s[_i])) { while (_i < _s.Length && char.IsDigit(_s[_i])) _i++; }
                else _i = save;
            }
            return double.Parse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        string ReadString(char quote)
        {
            _i++;
            var sb = new StringBuilder();
            while (_i < _s.Length && _s[_i] != quote)
            {
                char c = _s[_i++];
                if (c != '\\') { sb.Append(c); continue; }
                if (_i >= _s.Length) break;
                char e = _s[_i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'v': sb.Append('\v'); break;
                    case '0': sb.Append('\0'); break;
                    case 'u':
                        if (_i + 4 <= _s.Length && int.TryParse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int u))
                        {
                            sb.Append((char)u);
                            _i += 4;
                        }
                        break;
                    case 'x':
                        if (_i + 2 <= _s.Length && int.TryParse(_s.Substring(_i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int x))
                        {
                            sb.Append((char)x);
                            _i += 2;
                        }
                        break;
                    case '\r':
                        if (_i < _s.Length && _s[_i] == '\n') _i++;
                        break;
                    case '\n': break;
                    default: sb.Append(e); break;
                }
            }
            _i++; // closing quote
            return sb.ToString();
        }
    }

    public sealed class JsSyntaxError : Exception
    {
        public JsSyntaxError(string message, int pos) : base($"{message} (at {pos})") { }
    }

    // ====================================================================== AST

    abstract class Node { public int Pos; }

    abstract class Expr : Node { }
    sealed class NumLit : Expr { public double Value; }
    sealed class StrLit : Expr { public string Value; }
    sealed class BoolLit : Expr { public bool Value; }
    sealed class NullLit : Expr { }
    sealed class UndefinedLit : Expr { }
    sealed class ThisExpr : Expr { }
    sealed class Ident : Expr { public string Name; }
    sealed class ArrayLit : Expr { public List<Expr> Items = new List<Expr>(); }
    sealed class ObjectLit : Expr { public List<KeyValuePair<string, Expr>> Props = new List<KeyValuePair<string, Expr>>(); }
    sealed class MemberExpr : Expr { public Expr Obj; public string Name; }
    sealed class IndexExpr : Expr { public Expr Obj; public Expr Index; }
    sealed class CallExpr : Expr { public Expr Callee; public List<Expr> Args = new List<Expr>(); }
    sealed class NewExpr : Expr { public Expr Callee; public List<Expr> Args = new List<Expr>(); }
    sealed class UnaryExpr : Expr { public string Op; public Expr Operand; }
    sealed class UpdateExpr : Expr { public string Op; public bool Prefix; public Expr Target; }
    sealed class BinaryExpr : Expr { public string Op; public Expr L, R; }
    sealed class LogicalExpr : Expr { public string Op; public Expr L, R; }
    sealed class AssignExpr : Expr { public string Op; public Expr Target, Value; }
    sealed class CondExpr : Expr { public Expr Test, Then, Else; }
    sealed class SeqExpr : Expr { public List<Expr> Items = new List<Expr>(); }
    sealed class FuncExpr : Expr
    {
        public string Name;
        public List<string> Params = new List<string>();
        public List<Stmt> Body;
        public Expr ExprBody;      // arrow function with expression body
        public bool Arrow;
    }

    abstract class Stmt : Node { }
    sealed class VarDecl : Stmt { public List<KeyValuePair<string, Expr>> Decls = new List<KeyValuePair<string, Expr>>(); }
    sealed class ExprStmt : Stmt { public Expr E; }
    sealed class IfStmt : Stmt { public Expr Test; public Stmt Then, Else; }
    sealed class ForStmt : Stmt { public Stmt Init; public Expr Test, Update; public Stmt Body; }
    sealed class ForInStmt : Stmt { public string Var; public Expr Obj; public Stmt Body; public bool Of; }
    sealed class WhileStmt : Stmt { public Expr Test; public Stmt Body; }
    sealed class DoWhileStmt : Stmt { public Expr Test; public Stmt Body; }
    sealed class ReturnStmt : Stmt { public Expr Value; }
    sealed class BreakStmt : Stmt { }
    sealed class ContinueStmt : Stmt { }
    sealed class BlockStmt : Stmt { public List<Stmt> Body = new List<Stmt>(); }
    sealed class FuncDecl : Stmt { public FuncExpr Func; }
    sealed class SwitchStmt : Stmt { public Expr Disc; public List<KeyValuePair<Expr, List<Stmt>>> Cases = new List<KeyValuePair<Expr, List<Stmt>>>(); }
    sealed class TryStmt : Stmt { public BlockStmt Block, Handler, Finalizer; public string Param; }
    sealed class ThrowStmt : Stmt { public Expr Value; }
    sealed class EmptyStmt : Stmt { }

    // ====================================================================== parser

    /// <summary>Recursive-descent parser producing a small AST (ES5 plus let/const, arrow functions, **).</summary>
    sealed class JsParser
    {
        readonly List<Token> _t;
        int _p;

        JsParser(List<Token> tokens) { _t = tokens; }

        public static List<Stmt> Parse(string source)
        {
            var lexer = new JsLexer(source);
            var p = new JsParser(lexer.Tokens);
            var list = new List<Stmt>();
            while (!p.AtEnd) list.Add(p.Statement());
            return list;
        }

        Token Cur => _t[_p];
        bool AtEnd => _t[_p].Type == Tok.End;
        Token Next() => _t[_p++];
        bool IsP(string s) => Cur.Type == Tok.Punct && Cur.Text == s;
        bool IsK(string s) => Cur.Type == Tok.Ident && Cur.Text == s;
        bool PeekP(int k, string s) => _p + k < _t.Count && _t[_p + k].Type == Tok.Punct && _t[_p + k].Text == s;

        bool EatP(string s)
        {
            if (!IsP(s)) return false;
            _p++;
            return true;
        }

        void ExpectP(string s)
        {
            if (!EatP(s)) throw new JsSyntaxError($"Expected '{s}' but found '{Cur.Text ?? Cur.Type.ToString()}'", Cur.Pos);
        }

        string ExpectIdent()
        {
            if (Cur.Type != Tok.Ident) throw new JsSyntaxError($"Expected identifier but found '{Cur.Text}'", Cur.Pos);
            return Next().Text;
        }

        void Semi()
        {
            if (EatP(";")) return;
            // Automatic semicolon insertion.
            if (IsP("}") || AtEnd || Cur.NewlineBefore) return;
            throw new JsSyntaxError($"Expected ';' but found '{Cur.Text}'", Cur.Pos);
        }

        Stmt Statement()
        {
            int pos = Cur.Pos;
            if (Cur.Type == Tok.Punct)
            {
                if (IsP("{")) return Block();
                if (EatP(";")) return new EmptyStmt { Pos = pos };
            }
            if (Cur.Type == Tok.Ident)
            {
                switch (Cur.Text)
                {
                    case "var":
                    case "let":
                    case "const":
                    {
                        _p++;
                        var d = VarList(pos);
                        Semi();
                        return d;
                    }
                    case "if":
                    {
                        _p++;
                        ExpectP("(");
                        var test = Expression();
                        ExpectP(")");
                        var then = Statement();
                        Stmt els = null;
                        if (IsK("else")) { _p++; els = Statement(); }
                        return new IfStmt { Pos = pos, Test = test, Then = then, Else = els };
                    }
                    case "for": return ForStatement(pos);
                    case "while":
                    {
                        _p++;
                        ExpectP("(");
                        var test = Expression();
                        ExpectP(")");
                        return new WhileStmt { Pos = pos, Test = test, Body = Statement() };
                    }
                    case "do":
                    {
                        _p++;
                        var body = Statement();
                        if (!IsK("while")) throw new JsSyntaxError("Expected 'while'", Cur.Pos);
                        _p++;
                        ExpectP("(");
                        var test = Expression();
                        ExpectP(")");
                        EatP(";");
                        return new DoWhileStmt { Pos = pos, Test = test, Body = body };
                    }
                    case "return":
                    {
                        _p++;
                        Expr v = null;
                        if (!IsP(";") && !IsP("}") && !AtEnd && !Cur.NewlineBefore) v = Expression();
                        Semi();
                        return new ReturnStmt { Pos = pos, Value = v };
                    }
                    case "break":
                        _p++;
                        if (Cur.Type == Tok.Ident && !Cur.NewlineBefore) _p++; // label (ignored)
                        Semi();
                        return new BreakStmt { Pos = pos };
                    case "continue":
                        _p++;
                        if (Cur.Type == Tok.Ident && !Cur.NewlineBefore) _p++;
                        Semi();
                        return new ContinueStmt { Pos = pos };
                    case "function":
                        if (_p + 1 < _t.Count && _t[_p + 1].Type == Tok.Ident)
                        {
                            _p++;
                            var f = FunctionRest(pos);
                            return new FuncDecl { Pos = pos, Func = f };
                        }
                        break;
                    case "switch": return SwitchStatement(pos);
                    case "try": return TryStatement(pos);
                    case "throw":
                    {
                        _p++;
                        var v = Expression();
                        Semi();
                        return new ThrowStmt { Pos = pos, Value = v };
                    }
                }
            }
            var e = Expression();
            Semi();
            return new ExprStmt { Pos = pos, E = e };
        }

        BlockStmt Block()
        {
            var b = new BlockStmt { Pos = Cur.Pos };
            ExpectP("{");
            while (!IsP("}") && !AtEnd) b.Body.Add(Statement());
            ExpectP("}");
            return b;
        }

        VarDecl VarList(int pos, bool noIn = false)
        {
            var d = new VarDecl { Pos = pos };
            do
            {
                string name = ExpectIdent();
                Expr init = null;
                if (EatP("=")) init = Assignment(noIn);
                d.Decls.Add(new KeyValuePair<string, Expr>(name, init));
            } while (EatP(","));
            return d;
        }

        Stmt ForStatement(int pos)
        {
            _p++;
            ExpectP("(");
            Stmt init = null;
            if (IsK("var") || IsK("let") || IsK("const"))
            {
                int vp = Cur.Pos;
                _p++;
                if (_p + 1 < _t.Count && _t[_p].Type == Tok.Ident && _t[_p + 1].Type == Tok.Ident &&
                    (_t[_p + 1].Text == "in" || _t[_p + 1].Text == "of"))
                {
                    string v = Next().Text;
                    bool of = Next().Text == "of";
                    var obj = Expression();
                    ExpectP(")");
                    return new ForInStmt { Pos = pos, Var = v, Obj = obj, Body = Statement(), Of = of };
                }
                init = VarList(vp, true);
            }
            else if (!IsP(";"))
            {
                if (Cur.Type == Tok.Ident && _p + 1 < _t.Count && _t[_p + 1].Type == Tok.Ident &&
                    (_t[_p + 1].Text == "in" || _t[_p + 1].Text == "of"))
                {
                    string v = Next().Text;
                    bool of = Next().Text == "of";
                    var obj = Expression();
                    ExpectP(")");
                    return new ForInStmt { Pos = pos, Var = v, Obj = obj, Body = Statement(), Of = of };
                }
                init = new ExprStmt { Pos = Cur.Pos, E = Expression(true) };
            }
            ExpectP(";");
            Expr test = IsP(";") ? null : Expression();
            ExpectP(";");
            Expr update = IsP(")") ? null : Expression();
            ExpectP(")");
            return new ForStmt { Pos = pos, Init = init, Test = test, Update = update, Body = Statement() };
        }

        Stmt SwitchStatement(int pos)
        {
            _p++;
            ExpectP("(");
            var s = new SwitchStmt { Pos = pos, Disc = Expression() };
            ExpectP(")");
            ExpectP("{");
            while (!IsP("}") && !AtEnd)
            {
                Expr test = null;
                if (IsK("case")) { _p++; test = Expression(); }
                else if (IsK("default")) _p++;
                else throw new JsSyntaxError("Expected 'case' or 'default'", Cur.Pos);
                ExpectP(":");
                var body = new List<Stmt>();
                while (!IsK("case") && !IsK("default") && !IsP("}") && !AtEnd) body.Add(Statement());
                s.Cases.Add(new KeyValuePair<Expr, List<Stmt>>(test, body));
            }
            ExpectP("}");
            return s;
        }

        Stmt TryStatement(int pos)
        {
            _p++;
            var t = new TryStmt { Pos = pos, Block = Block() };
            if (IsK("catch"))
            {
                _p++;
                if (EatP("("))
                {
                    t.Param = ExpectIdent();
                    ExpectP(")");
                }
                t.Handler = Block();
            }
            if (IsK("finally"))
            {
                _p++;
                t.Finalizer = Block();
            }
            return t;
        }

        FuncExpr FunctionRest(int pos)
        {
            var f = new FuncExpr { Pos = pos };
            if (Cur.Type == Tok.Ident) f.Name = Next().Text;
            ExpectP("(");
            if (!IsP(")"))
            {
                do
                {
                    EatP("...");
                    f.Params.Add(ExpectIdent());
                    if (EatP("=")) Assignment(); // default values ignored
                } while (EatP(","));
            }
            ExpectP(")");
            f.Body = Block().Body;
            return f;
        }

        // ------------------------------------------------------------------ expressions

        Expr Expression(bool noIn = false)
        {
            var e = Assignment(noIn);
            if (!IsP(",")) return e;
            var seq = new SeqExpr { Pos = e.Pos };
            seq.Items.Add(e);
            while (EatP(",")) seq.Items.Add(Assignment(noIn));
            return seq;
        }

        bool IsArrowAhead()
        {
            // ident =>   |   ( [ident [, ident]*] ) =>
            if (Cur.Type == Tok.Ident && PeekP(1, "=>")) return true;
            if (!IsP("(")) return false;
            int k = _p + 1;
            int depth = 1;
            while (k < _t.Count && depth > 0)
            {
                var t = _t[k];
                if (t.Type == Tok.Punct && t.Text == "(") depth++;
                else if (t.Type == Tok.Punct && t.Text == ")") depth--;
                else if (t.Type == Tok.End) return false;
                k++;
            }
            return k < _t.Count && _t[k].Type == Tok.Punct && _t[k].Text == "=>";
        }

        Expr ArrowFunction()
        {
            var f = new FuncExpr { Pos = Cur.Pos, Arrow = true };
            if (Cur.Type == Tok.Ident) f.Params.Add(Next().Text);
            else
            {
                ExpectP("(");
                if (!IsP(")"))
                {
                    do
                    {
                        EatP("...");
                        f.Params.Add(ExpectIdent());
                        if (EatP("=")) Assignment();
                    } while (EatP(","));
                }
                ExpectP(")");
            }
            ExpectP("=>");
            if (IsP("{")) f.Body = Block().Body;
            else f.ExprBody = Assignment();
            return f;
        }

        static readonly HashSet<string> AssignOps = new HashSet<string>
            { "=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=", ">>>=", "**=" };

        Expr Assignment(bool noIn = false)
        {
            if (IsArrowAhead()) return ArrowFunction();
            var left = Conditional(noIn);
            if (Cur.Type == Tok.Punct && AssignOps.Contains(Cur.Text))
            {
                if (!(left is Ident || left is MemberExpr || left is IndexExpr))
                    throw new JsSyntaxError("Invalid assignment target", Cur.Pos);
                string op = Next().Text;
                var value = Assignment(noIn);
                return new AssignExpr { Pos = left.Pos, Op = op, Target = left, Value = value };
            }
            return left;
        }

        Expr Conditional(bool noIn)
        {
            var test = Binary(0, noIn);
            if (!EatP("?")) return test;
            var then = Assignment();
            ExpectP(":");
            var els = Assignment(noIn);
            return new CondExpr { Pos = test.Pos, Test = test, Then = then, Else = els };
        }

        static int Precedence(string op, bool noIn)
        {
            switch (op)
            {
                case "??": return 1;
                case "||": return 2;
                case "&&": return 3;
                case "|": return 4;
                case "^": return 5;
                case "&": return 6;
                case "==": case "!=": case "===": case "!==": return 7;
                case "<": case ">": case "<=": case ">=": case "instanceof": return 8;
                case "in": return noIn ? -1 : 8;
                case "<<": case ">>": case ">>>": return 9;
                case "+": case "-": return 10;
                case "*": case "/": case "%": return 11;
                case "**": return 12;
                default: return -1;
            }
        }

        Expr Binary(int minPrec, bool noIn)
        {
            var left = Unary();
            while (true)
            {
                if (Cur.Type != Tok.Punct && !(Cur.Type == Tok.Ident && (Cur.Text == "instanceof" || Cur.Text == "in"))) break;
                string op = Cur.Text;
                int prec = Precedence(op, noIn);
                if (prec < 0 || prec < minPrec) break;
                _p++;
                // ** is right associative
                var right = op == "**" ? Binary(prec, noIn) : Binary(prec + 1, noIn);
                if (op == "&&" || op == "||" || op == "??") left = new LogicalExpr { Pos = left.Pos, Op = op, L = left, R = right };
                else left = new BinaryExpr { Pos = left.Pos, Op = op, L = left, R = right };
            }
            return left;
        }

        Expr Unary()
        {
            int pos = Cur.Pos;
            if (Cur.Type == Tok.Punct)
            {
                switch (Cur.Text)
                {
                    case "!":
                    case "-":
                    case "+":
                    case "~":
                    {
                        string op = Next().Text;
                        return new UnaryExpr { Pos = pos, Op = op, Operand = Unary() };
                    }
                    case "++":
                    case "--":
                    {
                        string op = Next().Text;
                        return new UpdateExpr { Pos = pos, Op = op, Prefix = true, Target = Unary() };
                    }
                }
            }
            else if (Cur.Type == Tok.Ident && (Cur.Text == "typeof" || Cur.Text == "void" || Cur.Text == "delete"))
            {
                string op = Next().Text;
                return new UnaryExpr { Pos = pos, Op = op, Operand = Unary() };
            }
            var e = Postfix();
            return e;
        }

        Expr Postfix()
        {
            var e = CallMember();
            if (Cur.Type == Tok.Punct && (Cur.Text == "++" || Cur.Text == "--") && !Cur.NewlineBefore)
            {
                string op = Next().Text;
                return new UpdateExpr { Pos = e.Pos, Op = op, Prefix = false, Target = e };
            }
            return e;
        }

        Expr CallMember()
        {
            Expr e;
            if (IsK("new"))
            {
                int pos = Next().Pos;
                var callee = MemberOnly();
                var n = new NewExpr { Pos = pos, Callee = callee };
                if (EatP("(")) n.Args = Arguments();
                e = n;
            }
            else e = Primary();
            while (true)
            {
                if (EatP("."))
                {
                    string name = ExpectIdent();
                    e = new MemberExpr { Pos = e.Pos, Obj = e, Name = name };
                }
                else if (EatP("["))
                {
                    var idx = Expression();
                    ExpectP("]");
                    e = new IndexExpr { Pos = e.Pos, Obj = e, Index = idx };
                }
                else if (IsP("("))
                {
                    _p++;
                    e = new CallExpr { Pos = e.Pos, Callee = e, Args = Arguments() };
                }
                else break;
            }
            return e;
        }

        Expr MemberOnly()
        {
            var e = Primary();
            while (true)
            {
                if (EatP("."))
                {
                    string name = ExpectIdent();
                    e = new MemberExpr { Pos = e.Pos, Obj = e, Name = name };
                }
                else if (EatP("["))
                {
                    var idx = Expression();
                    ExpectP("]");
                    e = new IndexExpr { Pos = e.Pos, Obj = e, Index = idx };
                }
                else break;
            }
            return e;
        }

        List<Expr> Arguments()
        {
            var list = new List<Expr>();
            if (EatP(")")) return list;
            do
            {
                if (IsP(")")) break; // trailing comma
                EatP("...");
                list.Add(Assignment());
            } while (EatP(","));
            ExpectP(")");
            return list;
        }

        Expr Primary()
        {
            var t = Cur;
            switch (t.Type)
            {
                case Tok.Num:
                    _p++;
                    return new NumLit { Pos = t.Pos, Value = t.Num };
                case Tok.Str:
                    _p++;
                    return new StrLit { Pos = t.Pos, Value = t.Text };
                case Tok.Ident:
                    _p++;
                    switch (t.Text)
                    {
                        case "true": return new BoolLit { Pos = t.Pos, Value = true };
                        case "false": return new BoolLit { Pos = t.Pos, Value = false };
                        case "null": return new NullLit { Pos = t.Pos };
                        case "undefined": return new UndefinedLit { Pos = t.Pos };
                        case "this": return new ThisExpr { Pos = t.Pos };
                        case "function": return FunctionRest(t.Pos);
                    }
                    return new Ident { Pos = t.Pos, Name = t.Text };
                case Tok.Punct:
                    if (t.Text == "(")
                    {
                        _p++;
                        var e = Expression();
                        ExpectP(")");
                        return e;
                    }
                    if (t.Text == "[")
                    {
                        _p++;
                        var a = new ArrayLit { Pos = t.Pos };
                        while (!IsP("]"))
                        {
                            if (IsP(",")) { _p++; a.Items.Add(new UndefinedLit { Pos = Cur.Pos }); continue; }
                            EatP("...");
                            a.Items.Add(Assignment());
                            if (!EatP(",")) break;
                        }
                        ExpectP("]");
                        return a;
                    }
                    if (t.Text == "{")
                    {
                        _p++;
                        var o = new ObjectLit { Pos = t.Pos };
                        while (!IsP("}"))
                        {
                            string key;
                            if (Cur.Type == Tok.Ident || Cur.Type == Tok.Str) key = Next().Text;
                            else if (Cur.Type == Tok.Num) key = JsValue.NumberToString(Next().Num);
                            else throw new JsSyntaxError("Invalid property name", Cur.Pos);
                            Expr v;
                            if (EatP(":")) v = Assignment();
                            else v = new Ident { Pos = Cur.Pos, Name = key }; // shorthand
                            o.Props.Add(new KeyValuePair<string, Expr>(key, v));
                            if (!EatP(",")) break;
                        }
                        ExpectP("}");
                        return o;
                    }
                    break;
            }
            throw new JsSyntaxError($"Unexpected token '{t.Text ?? t.Type.ToString()}'", t.Pos);
        }
    }
}
