﻿namespace LanguageServer
{
    using Algorithms;
    using Antlr4.Runtime;
    using Antlr4.Runtime.Misc;
    using Antlr4.Runtime.Tree;
    using GrammarGrammar;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using Document = Workspaces.Document;

    public class Transform
    {
        private static object ule_alt_list;

        private class ExtractGrammarType : ANTLRv4ParserBaseListener
        {
            public enum GrammarType
            {
                Combined,
                Parser,
                Lexer,
                NotAGrammar
            }

            public GrammarType Type;

            public ExtractGrammarType()
            {
            }

            public override void EnterGrammarType([NotNull] ANTLRv4Parser.GrammarTypeContext context)
            {
                if (context.GetChild(0).GetText() == "parser")
                {
                    Type = GrammarType.Parser;
                }
                else if (context.GetChild(0).GetText() == "lexer")
                {
                    Type = GrammarType.Lexer;
                }
                else
                {
                    Type = GrammarType.Combined;
                }
            }
        }

        private class LiteralsGrammar : ANTLRv4ParserBaseListener
        {
            public List<TerminalNodeImpl> Literals = new List<TerminalNodeImpl>();
            private readonly AntlrGrammarDetails _pd;

            public LiteralsGrammar(AntlrGrammarDetails pd)
            {
                _pd = pd;
            }

            public override void EnterTerminal([NotNull] ANTLRv4Parser.TerminalContext context)
            {
                TerminalNodeImpl first = context.GetChild(0) as TerminalNodeImpl;
                if (first.Symbol.Type == ANTLRv4Parser.STRING_LITERAL)
                {
                    Literals.Add(first);
                }
            }
        }

        private class FindFirstRule : ANTLRv4ParserBaseListener
        {
            public IParseTree First = null;
            public IParseTree Last = null;

            public FindFirstRule() { }

            public override void EnterRules([NotNull] ANTLRv4Parser.RulesContext context)
            {
                ANTLRv4Parser.RuleSpecContext[] rule_spec = context.ruleSpec();
                if (rule_spec == null)
                {
                    return;
                }

                First = rule_spec[0];
            }
        }

        private class FindFirstMode : ANTLRv4ParserBaseListener
        {
            public IParseTree First = null;
            public IParseTree Last = null;

            public FindFirstMode() { }


            public override void EnterModeSpec([NotNull] ANTLRv4Parser.ModeSpecContext context)
            {
                First = context;
            }
        }

        private class FindOptions : ANTLRv4ParserBaseListener
        {
            public IParseTree OptionsSpec = null;
            public List<IParseTree> Options = new List<IParseTree>();

            public override void EnterOption([NotNull] ANTLRv4Parser.OptionContext context)
            {
                Options.Add(context);
                base.EnterOption(context);
            }

            public override void EnterOptionsSpec([NotNull] ANTLRv4Parser.OptionsSpecContext context)
            {
                OptionsSpec = context;
                base.EnterOptionsSpec(context);
            }
        }

        private class ExtractRules : ANTLRv4ParserBaseListener
        {
            public List<ANTLRv4Parser.ParserRuleSpecContext> ParserRules = new List<ANTLRv4Parser.ParserRuleSpecContext>();
            public List<ANTLRv4Parser.LexerRuleSpecContext> LexerRules = new List<ANTLRv4Parser.LexerRuleSpecContext>();
            public List<IParseTree> Rules = new List<IParseTree>();
            public List<ITerminalNode> LhsSymbol = new List<ITerminalNode>();
            public Dictionary<ITerminalNode, List<ITerminalNode>> RhsSymbols = new Dictionary<ITerminalNode, List<ITerminalNode>>();
            private ITerminalNode current_nonterminal;

            public override void EnterParserRuleSpec([NotNull] ANTLRv4Parser.ParserRuleSpecContext context)
            {
                ParserRules.Add(context);
                Rules.Add(context);
                ITerminalNode rule_ref = context.RULE_REF();
                LhsSymbol.Add(rule_ref);
                current_nonterminal = rule_ref;
                RhsSymbols[current_nonterminal] = new List<ITerminalNode>();
            }

            public override void EnterLexerRuleSpec([NotNull] ANTLRv4Parser.LexerRuleSpecContext context)
            {
                LexerRules.Add(context);
                Rules.Add(context);
                ITerminalNode token_ref = context.TOKEN_REF();
                LhsSymbol.Add(token_ref);
                current_nonterminal = token_ref;
                RhsSymbols[current_nonterminal] = new List<ITerminalNode>();
            }

            public override void EnterRuleref([NotNull] ANTLRv4Parser.RulerefContext context)
            {
                RhsSymbols[current_nonterminal].Add(context.GetChild(0) as ITerminalNode);
            }
        }

        private class ExtractModes : ANTLRv4ParserBaseListener
        {
            public List<ANTLRv4Parser.ModeSpecContext> Modes = new List<ANTLRv4Parser.ModeSpecContext>();

            public override void EnterModeSpec([NotNull] ANTLRv4Parser.ModeSpecContext context)
            {
                Modes.Add(context);
            }
        }

        private class FindCalls : CSharpSyntaxWalker
        {
            public List<string> Invocations = new List<string>();

            public override void VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                Invocations.Add(node.ToString());
                base.VisitInvocationExpression(node);
            }
        }

        private static Dictionary<string, SyntaxTree> ReadCsharpSource(Document document)
        {
            Dictionary<string, SyntaxTree> trees = new Dictionary<string, SyntaxTree>();
            string g4_file_path = document.FullPath;
            string current_dir = Path.GetDirectoryName(g4_file_path);
            if (current_dir == null)
            {
                return trees;
            }
            foreach (string f in Directory.EnumerateFiles(current_dir))
            {
                if (Path.GetExtension(f).ToLower() != ".cs")
                {
                    continue;
                }

                string file_name = f;
                string suffix = Path.GetExtension(file_name);
                if (suffix != ".cs")
                {
                    continue;
                }

                try
                {
                    string ffn = file_name;
                    StreamReader sr = new StreamReader(ffn);
                    string code = sr.ReadToEnd();
                    SyntaxTree tree = CSharpSyntaxTree.ParseText(code);
                    trees[ffn] = tree;
                }
                catch (Exception)
                {
                }
            }
            return trees;
        }

        private class TableOfRules
        {
            public class Row
            {
                public IParseTree rule;
                public string LHS;
                public List<string> RHS;
                public int start_index;
                public int end_index;
                public bool is_start;
                public bool is_used;
                public bool is_parser_rule;
            }

            public List<Row> rules = new List<Row>();
            public Dictionary<string, int> nt_to_index = new Dictionary<string, int>();
            public ExtractRules listener;
            private readonly AntlrGrammarDetails pd_parser;
            private readonly Document document;
            private readonly Dictionary<string, SyntaxTree> trees;

            public TableOfRules(AntlrGrammarDetails p, Document d)
            {
                pd_parser = p;
                document = d;
                trees = ReadCsharpSource(document);
            }

            public void ReadRules()
            {
                // Get rules, lhs, rhs.
                listener = new ExtractRules();
                ParseTreeWalker.Default.Walk(listener, pd_parser.ParseTree);
                List<ITerminalNode> nonterminals = listener.LhsSymbol;
                Dictionary<ITerminalNode, List<ITerminalNode>> rhs = listener.RhsSymbols;
                for (int i = 0; i < listener.Rules.Count; ++i)
                {
                    rules.Add(new Row()
                    {
                        rule = listener.Rules[i],
                        LHS = nonterminals[i].GetText(),
                        is_parser_rule = char.IsLower(nonterminals[i].GetText()[0]),
                        RHS = rhs[nonterminals[i]].Select(t => t.GetText()).ToList(),
                    });
                }
                for (int i = 0; i < rules.Count; ++i)
                {
                    string t = rules[i].LHS;
                    nt_to_index[t] = i;
                }
            }

            public void FindPartitions()
            {
                FindFirstRule find_first_rule = new FindFirstRule();
                ParseTreeWalker.Default.Walk(find_first_rule, pd_parser.ParseTree);
                IParseTree first_rule = find_first_rule.First;
                if (first_rule == null)
                {
                    return;
                }

                int insertion = first_rule.SourceInterval.a;
                Antlr4.Runtime.IToken insertion_tok = pd_parser.TokStream.Get(insertion);
                int insertion_ind = insertion_tok.StartIndex;
                string old_code = document.Code;
                for (int i = 0; i < rules.Count; ++i)
                {
                    IParseTree rule = rules[i].rule;
                    // Find range indices for rule including comments. Note, start index is inclusive; end
                    // index is exclusive. We make the assumption
                    // that the preceeding whitespace and comments are grouped with a rule all the way
                    // from the end a previous non-whitespace or comment, such as options, headers, or rule.
                    Interval token_interval = rule.SourceInterval;
                    int end = token_interval.b;
                    Antlr4.Runtime.IToken end_tok = pd_parser.TokStream.Get(end);
                    Antlr4.Runtime.IToken last = end_tok;
                    int end_ind = old_code.Length <= last.StopIndex ? last.StopIndex : last.StopIndex + 1;
                    for (int j = end_ind; j < old_code.Length; j++)
                    {
                        if (old_code[j] == '\r')
                        {
                            if (j + 1 < old_code.Length && old_code[j + 1] == '\n')
                            {
                                end_ind = j + 2;
                            }
                            else
                            {
                                end_ind = j + 1;
                            }

                            break;
                        }
                        end_ind = j;
                    }
                    IList<Antlr4.Runtime.IToken> inter = pd_parser.TokStream.GetHiddenTokensToRight(end_tok.TokenIndex);
                    int start = token_interval.a;
                    Antlr4.Runtime.IToken start_tok = pd_parser.TokStream.Get(start);
                    int start_ind = start_tok.StartIndex;
                    rules[i].start_index = start_ind;
                    rules[i].end_index = end_ind;
                }
                for (int i = 0; i < rules.Count; ++i)
                {
                    if (i > 0)
                    {
                        rules[i].start_index = rules[i - 1].end_index;
                    }
                }
                for (int i = 0; i < rules.Count; ++i)
                {
                    for (int j = rules[i].start_index; j < rules[i].end_index; ++j)
                    {
                        if (old_code[j] == '\r')
                        {
                            if (j + 1 < rules[i].end_index && old_code[j + 1] == '\n')
                            {
                                ;
                            }
                            else
                            {
                            }
                        }
                    }
                }
            }

            public void FindModePartitions()
            {
                FindFirstMode find_first_mode = new FindFirstMode();
                ParseTreeWalker.Default.Walk(find_first_mode, pd_parser.ParseTree);
                IParseTree first_rule = find_first_mode.First;
                if (first_rule == null)
                {
                    return;
                }

                int insertion = first_rule.SourceInterval.a;
                Antlr4.Runtime.IToken insertion_tok = pd_parser.TokStream.Get(insertion);
                int insertion_ind = insertion_tok.StartIndex;
                string old_code = document.Code;
                for (int i = 0; i < rules.Count; ++i)
                {
                    IParseTree rule = rules[i].rule;
                    // Find range indices for rule including comments. Note, start index is inclusive; end
                    // index is exclusive. We make the assumption
                    // that the preceeding whitespace and comments are grouped with a rule all the way
                    // from the end a previous non-whitespace or comment, such as options, headers, or rule.
                    Interval token_interval = rule.SourceInterval;
                    int end = token_interval.b;
                    Antlr4.Runtime.IToken end_tok = pd_parser.TokStream.Get(end);
                    Antlr4.Runtime.IToken last = end_tok;
                    int end_ind = old_code.Length <= last.StopIndex ? last.StopIndex : last.StopIndex + 1;
                    for (int j = end_ind; j < old_code.Length; j++)
                    {
                        if (old_code[j] == '\r')
                        {
                            if (j + 1 < old_code.Length && old_code[j + 1] == '\n')
                            {
                                end_ind = j + 2;
                            }
                            else
                            {
                                end_ind = j + 1;
                            }

                            break;
                        }
                        end_ind = j;
                    }
                    IList<Antlr4.Runtime.IToken> inter = pd_parser.TokStream.GetHiddenTokensToRight(end_tok.TokenIndex);
                    int start = token_interval.a;
                    Antlr4.Runtime.IToken start_tok = pd_parser.TokStream.Get(start);
                    int start_ind = start_tok.StartIndex;
                    rules[i].start_index = start_ind;
                    rules[i].end_index = end_ind;
                }
                for (int i = 0; i < rules.Count; ++i)
                {
                    if (i > 0)
                    {
                        rules[i].start_index = rules[i - 1].end_index;
                    }
                }
                for (int i = 0; i < rules.Count; ++i)
                {
                    for (int j = rules[i].start_index; j < rules[i].end_index; ++j)
                    {
                        if (old_code[j] == '\r')
                        {
                            if (j + 1 < rules[i].end_index && old_code[j + 1] == '\n')
                            {
                                ;
                            }
                            else
                            {
                            }
                        }
                    }
                }
            }

            public void FindStartRules()
            {
                List<ITerminalNode> lhs = listener.LhsSymbol;
                for (int i = 0; i < rules.Count; ++i)
                {
                    for (int j = 0; j < rules[i].RHS.Count; ++j)
                    {
                        string rhs_symbol = rules[i].RHS[j];
                        if (nt_to_index.ContainsKey(rhs_symbol))
                            rules[nt_to_index[rules[i].RHS[j]]].is_used = true;
                    }
                }
                try
                {
                    foreach (KeyValuePair<string, SyntaxTree> kvp in trees)
                    {
                        string file_name = kvp.Key;
                        SyntaxTree tree = kvp.Value;
                        CompilationUnitSyntax root = (CompilationUnitSyntax)tree.GetRoot();
                        if (root == null)
                        {
                            continue;
                        }
                        FindCalls syntax_walker = new FindCalls();
                        syntax_walker.Visit(root);
                        for (int i = 0; i < rules.Count; ++i)
                        {
                            string nt_name = rules[i].LHS;
                            string call = "." + nt_name + "()";
                            foreach (string j in syntax_walker.Invocations)
                            {
                                if (j.Contains(call))
                                {
                                    rules[i].is_used = true;
                                    rules[i].is_start = true;
                                }
                            }
                        }
                    }
                }
                catch
                {
                }
            }
        }

        private class TableOfModes
        {
            public class Row
            {
                public ANTLRv4Parser.ModeSpecContext mode;
                public string name;
                public int start_index;
                public int end_index;
            }

            public List<Row> modes = new List<Row>();
            public Dictionary<string, int> name_to_index = new Dictionary<string, int>();
            public ExtractModes listener;
            private readonly AntlrGrammarDetails pd_parser;
            private readonly Document document;
            private readonly Dictionary<string, SyntaxTree> trees;

            public TableOfModes(AntlrGrammarDetails p, Document d)
            {
                pd_parser = p;
                document = d;
                trees = ReadCsharpSource(document);
            }

            public void ReadModes()
            {
                // Get modes.
                listener = new ExtractModes();
                ParseTreeWalker.Default.Walk(listener, pd_parser.ParseTree);
                for (int i = 0; i < listener.Modes.Count; ++i)
                {
                    modes.Add(new Row()
                    {
                        mode = listener.Modes[i],
                        name = listener.Modes[i].identifier().GetText(),
                    });
                }
                for (int i = 0; i < modes.Count; ++i)
                {
                    string t = modes[i].name;
                    name_to_index[t] = i;
                }
            }

            public void FindPartitions()
            {
                FindFirstMode find_first_mode = new FindFirstMode();
                ParseTreeWalker.Default.Walk(find_first_mode, pd_parser.ParseTree);
                IParseTree first_rule = find_first_mode.First;
                if (first_rule == null)
                {
                    return;
                }

                int insertion = first_rule.SourceInterval.a;
                Antlr4.Runtime.IToken insertion_tok = pd_parser.TokStream.Get(insertion);
                int insertion_ind = insertion_tok.StartIndex;
                string old_code = document.Code;
                for (int i = 0; i < modes.Count; ++i)
                {
                    var mode = modes[i].mode;
                    // Find range indices for modes including comments. Note, start index is inclusive; end
                    // index is exclusive. We make the assumption
                    // that the preceeding whitespace and comments are grouped with a rule all the way
                    // from the end a previous non-whitespace or comment, such as options, headers, or rule.
                    Interval token_interval = mode.SourceInterval;
                    int end = token_interval.b;
                    Antlr4.Runtime.IToken end_tok = pd_parser.TokStream.Get(end);
                    Antlr4.Runtime.IToken last = end_tok;
                    int end_ind = old_code.Length <= last.StopIndex ? last.StopIndex : last.StopIndex + 1;
                    for (int j = end_ind; j < old_code.Length; j++)
                    {
                        if (old_code[j] == '\r')
                        {
                            if (j + 1 < old_code.Length && old_code[j + 1] == '\n')
                            {
                                end_ind = j + 2;
                            }
                            else
                            {
                                end_ind = j + 1;
                            }

                            break;
                        }
                        end_ind = j;
                    }
                    IList<Antlr4.Runtime.IToken> inter = pd_parser.TokStream.GetHiddenTokensToRight(end_tok.TokenIndex);
                    int start = token_interval.a;
                    Antlr4.Runtime.IToken start_tok = pd_parser.TokStream.Get(start);
                    int start_ind = start_tok.StartIndex;
                    modes[i].start_index = start_ind;
                    modes[i].end_index = end_ind;
                }
                for (int i = 0; i < modes.Count; ++i)
                {
                    if (i > 0)
                    {
                        modes[i].start_index = modes[i - 1].end_index;
                    }
                }
                for (int i = 0; i < modes.Count; ++i)
                {
                    for (int j = modes[i].start_index; j < modes[i].end_index; ++j)
                    {
                        if (old_code[j] == '\r')
                        {
                            if (j + 1 < modes[i].end_index && old_code[j + 1] == '\n')
                            {
                                ;
                            }
                            else
                            {
                            }
                        }
                    }
                }
            }
        }

        public static void Reconstruct(StringBuilder sb, CommonTokenStream stream, IParseTree tree, ref int previous, Func<IParseTree, string> replace)
        {
            if (tree as TerminalNodeImpl != null)
            {
                TerminalNodeImpl tok = tree as TerminalNodeImpl;
                var start = tok.Payload.StartIndex;
                var stop = tok.Payload.StopIndex + 1;
                ICharStream charstream = tok.Payload.InputStream;
                if (previous < start)
                {
                    Interval previous_interval = new Interval(previous, start - 1);
                    string inter = charstream.GetText(previous_interval);
                    sb.Append(inter);
                }
                if (tok.Symbol.Type == TokenConstants.EOF)
                    return;
                string new_s = replace(tok);
                if (new_s != null)
                    sb.Append(new_s);
                else
                    sb.Append(tok.GetText());
                previous = stop;
            }
            else
            {
                var new_s = replace(tree);
                if (new_s != null)
                {
                    Interval source_interval = tree.SourceInterval;
                    int a = source_interval.a;
                    int b = source_interval.b;
                    IToken ta = stream.Get(a);
                    IToken tb = stream.Get(b);
                    var start = ta.StartIndex;
                    var stop = tb.StopIndex + 1;
                    ICharStream charstream = ta.InputStream;
                    if (previous < start)
                    {
                        Interval previous_interval = new Interval(previous, start - 1);
                        string inter = charstream.GetText(previous_interval);
                        sb.Append(inter);
                    }
                    sb.Append(new_s);
                    previous = stop;
                }
                else
                {
                    for (int i = 0; i < tree.ChildCount; ++i)
                    {
                        var c = tree.GetChild(i);
                        Reconstruct(sb, stream, c, ref previous, replace);
                    }
                }
            }
        }

        public static void Output(StringBuilder sb, CommonTokenStream stream, IParseTree tree)
        {
            if (tree as TerminalNodeImpl != null)
            {
                TerminalNodeImpl tok = tree as TerminalNodeImpl;
                if (tok.Symbol.Type == TokenConstants.EOF)
                    return;
                sb.Append(" " + tok.GetText());
            }
            else
            {
                for (int i = 0; i < tree.ChildCount; ++i)
                {
                    var c = tree.GetChild(i);
                    Output(sb, stream, c);
                }
            }
        }

        public static Dictionary<string, string> ReplaceLiterals(int index, Document document)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();

            // Check if initial file is a grammar.
            AntlrGrammarDetails pd_parser = ParserDetailsFactory.Create(document) as AntlrGrammarDetails;
            ExtractGrammarType egt = new ExtractGrammarType();
            ParseTreeWalker.Default.Walk(egt, pd_parser.ParseTree);
            bool is_grammar = egt.Type == ExtractGrammarType.GrammarType.Parser
                || egt.Type == ExtractGrammarType.GrammarType.Combined
                || egt.Type == ExtractGrammarType.GrammarType.Lexer;
            if (!is_grammar)
            {
                return result;
            }

            // Find all other grammars by walking dependencies (import, vocab, file names).
            HashSet<string> read_files = new HashSet<string>
            {
                document.FullPath
            };
            Dictionary<Workspaces.Document, List<TerminalNodeImpl>> every_damn_literal =
                new Dictionary<Workspaces.Document, List<TerminalNodeImpl>>();
            for (; ; )
            {
                int before_count = read_files.Count;
                foreach (string f in read_files)
                {
                    List<string> additional = AntlrGrammarDetails._dependent_grammars.Where(
                        t => t.Value.Contains(f)).Select(
                        t => t.Key).ToList();
                    read_files = read_files.Union(additional).ToHashSet();
                }
                foreach (string f in read_files)
                {
                    IEnumerable<List<string>> additional = AntlrGrammarDetails._dependent_grammars.Where(
                        t => t.Key == f).Select(
                        t => t.Value);
                    foreach (List<string> t in additional)
                    {
                        read_files = read_files.Union(t).ToHashSet();
                    }
                }
                int after_count = read_files.Count;
                if (after_count == before_count)
                {
                    break;
                }
            }

            // Find rewrite rules, i.e., string literal to symbol name.
            Dictionary<string, string> subs = new Dictionary<string, string>();
            foreach (string f in read_files)
            {
                Workspaces.Document whatever_document = Workspaces.Workspace.Instance.FindDocument(f);
                if (whatever_document == null)
                {
                    continue;
                }
                AntlrGrammarDetails pd_whatever = ParserDetailsFactory.Create(whatever_document) as AntlrGrammarDetails;

                // Find literals in grammars.
                LiteralsGrammar lp_whatever = new LiteralsGrammar(pd_whatever);
                ParseTreeWalker.Default.Walk(lp_whatever, pd_whatever.ParseTree);
                List<TerminalNodeImpl> list_literals = lp_whatever.Literals;
                foreach (TerminalNodeImpl lexer_literal in list_literals)
                {
                    string old_name = lexer_literal.GetText();
                    // Given candidate, walk up tree to find lexer_rule.
                    /*
                        ( ruleSpec
                          ( lexerRuleSpec
                            ( OFF_CHANNEL text=\r\n\r\n
                            )
                            ( OFF_CHANNEL text=...
                            )
                            (OFF_CHANNEL text =\r\n\r\n
                            )
                            (OFF_CHANNEL text =...
                            )
                            (OFF_CHANNEL text =\r\n\r\n
                            )
                            (DEFAULT_TOKEN_CHANNEL i = 995 txt = NONASSOC tt = 1
                            )
                            (OFF_CHANNEL text =\r\n\t
                            )
                            (DEFAULT_TOKEN_CHANNEL i = 997 txt =: tt = 29
                            )
                            (lexerRuleBlock
                              (lexerAltList
                                (lexerAlt
                                  (lexerElements
                                    (lexerElement
                                      (lexerAtom
                                        (terminal
                                          (OFF_CHANNEL text =
                                          )
                                          (DEFAULT_TOKEN_CHANNEL i = 999 txt = '%binary' tt = 8
                            ))))))))
                            (OFF_CHANNEL text =\r\n\t
                            )
                            (DEFAULT_TOKEN_CHANNEL i = 1001 txt =; tt = 32
                        ) ) )

                     * Make sure it fits the structure of the tree shown above.
                     * 
                     */
                    IRuleNode p1 = lexer_literal.Parent;
                    if (p1.ChildCount != 1)
                    {
                        continue;
                    }

                    if (!(p1 is ANTLRv4Parser.TerminalContext))
                    {
                        continue;
                    }

                    IRuleNode p2 = p1.Parent;
                    if (p2.ChildCount != 1)
                    {
                        continue;
                    }

                    if (!(p2 is ANTLRv4Parser.LexerAtomContext))
                    {
                        continue;
                    }

                    IRuleNode p3 = p2.Parent;
                    if (p3.ChildCount != 1)
                    {
                        continue;
                    }

                    if (!(p3 is ANTLRv4Parser.LexerElementContext))
                    {
                        continue;
                    }

                    IRuleNode p4 = p3.Parent;
                    if (p4.ChildCount != 1)
                    {
                        continue;
                    }

                    if (!(p4 is ANTLRv4Parser.LexerElementsContext))
                    {
                        continue;
                    }

                    IRuleNode p5 = p4.Parent;
                    if (p5.ChildCount != 1)
                    {
                        continue;
                    }

                    if (!(p5 is ANTLRv4Parser.LexerAltContext))
                    {
                        continue;
                    }

                    IRuleNode p6 = p5.Parent;
                    if (p6.ChildCount != 1)
                    {
                        continue;
                    }

                    if (!(p6 is ANTLRv4Parser.LexerAltListContext))
                    {
                        continue;
                    }

                    IRuleNode p7 = p6.Parent;
                    if (p7.ChildCount != 1)
                    {
                        continue;
                    }

                    if (!(p7 is ANTLRv4Parser.LexerRuleBlockContext))
                    {
                        continue;
                    }

                    IRuleNode p8 = p7.Parent;
                    if (p8.ChildCount != 4)
                    {
                        continue;
                    }

                    if (!(p8 is ANTLRv4Parser.LexerRuleSpecContext))
                    {
                        continue;
                    }

                    IParseTree alt = p8.GetChild(0);
                    string new_name = alt.GetText();
                    subs.Add(old_name, new_name);
                }
            }

            // Find string literals in parser and combined grammars and substitute.
            foreach (string f in read_files)
            {
                Workspaces.Document whatever_document = Workspaces.Workspace.Instance.FindDocument(f);
                if (whatever_document == null)
                {
                    continue;
                }
                AntlrGrammarDetails pd_whatever = ParserDetailsFactory.Create(whatever_document) as AntlrGrammarDetails;
                StringBuilder sb = new StringBuilder();
                int pre = 0;
                Reconstruct(sb, pd_parser.TokStream, pd_parser.ParseTree, ref pre,
                    n =>
                    {
                        if (!(n is TerminalNodeImpl))
                        {
                            return null;
                        }
                        var t = n as TerminalNodeImpl;
                        if (t.Payload.Type != ANTLRv4Lexer.STRING_LITERAL)
                        {
                            return t.GetText();
                        }
                        bool no = false;
                        // Make sure this literal does not appear in lexer rule.
                        for (IRuleNode p = t.Parent; p != null; p = p.Parent)
                        {
                            if (p is ANTLRv4Parser.LexerRuleSpecContext)
                            {
                                no = true;
                                break;
                            }
                        }
                        if (no)
                        {
                            return t.GetText();
                        }
                        var r = t.GetText();
                        subs.TryGetValue(r, out string value);
                        if (value != null)
                        {
                            r = value;
                        }
                        return r;
                    });
                var new_code = sb.ToString();
                if (new_code != pd_parser.Code)
                {
                    result.Add(f, new_code);
                }
            }
            return result;
        }

        public static Dictionary<string, string> RemoveUselessParserProductions(int pos, Document document)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();

            // Check if lexer grammar.
            AntlrGrammarDetails pd_parser = ParserDetailsFactory.Create(document) as AntlrGrammarDetails;
            ExtractGrammarType lp = new ExtractGrammarType();
            ParseTreeWalker.Default.Walk(lp, pd_parser.ParseTree);
            bool is_lexer = lp.Type == ExtractGrammarType.GrammarType.Lexer;
            if (is_lexer)
            {
                // We don't consider lexer grammars.
                return result;
            }

            // Consider only the target grammar.
            TableOfRules table = new TableOfRules(pd_parser, document);
            table.ReadRules();
            table.FindPartitions();
            table.FindStartRules();

            List<Pair<int, int>> deletions = new List<Pair<int, int>>();
            foreach (TableOfRules.Row r in table.rules)
            {
                if (r.is_parser_rule && r.is_used == false)
                {
                    deletions.Add(new Pair<int, int>(r.start_index, r.end_index));
                }
            }
            deletions = deletions.OrderBy(p => p.a).ThenBy(p => p.b).ToList();
            StringBuilder sb = new StringBuilder();
            int previous = 0;
            string old_code = document.Code;
            foreach (Pair<int, int> l in deletions)
            {
                int index_start = l.a;
                int len = l.b - l.a;
                string pre = old_code.Substring(previous, index_start - previous);
                sb.Append(pre);
                previous = index_start + len;
            }
            string rest = old_code.Substring(previous);
            sb.Append(rest);
            string new_code = sb.ToString();
            result.Add(document.FullPath, new_code);

            return result;
        }

        public static Dictionary<string, string> MoveStartRuleToTop(int pos, Document document)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();

            // Check if lexer grammar.
            AntlrGrammarDetails pd_parser = ParserDetailsFactory.Create(document) as AntlrGrammarDetails;
            ExtractGrammarType lp = new ExtractGrammarType();
            ParseTreeWalker.Default.Walk(lp, pd_parser.ParseTree);
            bool is_lexer = lp.Type == ExtractGrammarType.GrammarType.Lexer;
            if (is_lexer)
            {
                // We don't consider lexer grammars.
                return result;
            }

            // Consider only the target grammar.
            TableOfRules table = new TableOfRules(pd_parser, document);
            table.ReadRules();
            table.FindPartitions();
            table.FindStartRules();

            string old_code = document.Code;
            List<Pair<int, int>> move = new List<Pair<int, int>>();
            foreach (TableOfRules.Row r in table.rules)
            {
                if (r.is_parser_rule && r.is_start == true)
                {
                    move.Add(new Pair<int, int>(r.start_index, r.end_index));
                }
            }
            move = move.OrderBy(p => p.a).ThenBy(p => p.b).ToList();

            FindFirstRule find_first_rule = new FindFirstRule();
            ParseTreeWalker.Default.Walk(find_first_rule, pd_parser.ParseTree);
            IParseTree first_rule = find_first_rule.First;
            if (first_rule == null)
            {
                return result;
            }

            int insertion = first_rule.SourceInterval.a;
            Antlr4.Runtime.IToken insertion_tok = pd_parser.TokStream.Get(insertion);
            int insertion_ind = insertion_tok.StartIndex;
            if (move.Count == 1 && move[0].a == insertion_ind)
            {
                return result;
            }
            StringBuilder sb = new StringBuilder();
            int previous = 0;
            {
                int index_start = insertion_ind;
                int len = 0;
                string pre = old_code.Substring(previous, index_start - previous);
                sb.Append(pre);
                previous = index_start + len;
            }
            foreach (Pair<int, int> l in move)
            {
                int index_start = l.a;
                int len = l.b - l.a;
                string add = old_code.Substring(index_start, len);
                sb.Append(add);
            }
            foreach (Pair<int, int> l in move)
            {
                int index_start = l.a;
                int len = l.b - l.a;
                string pre = old_code.Substring(previous, index_start - previous);
                sb.Append(pre);
                previous = index_start + len;
            }
            string rest = old_code.Substring(previous);
            sb.Append(rest);
            string new_code = sb.ToString();
            result.Add(document.FullPath, new_code);

            return result;
        }

        public static Dictionary<string, string> ReorderParserRules(int pos, Document document, LspAntlr.ReorderType type)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();

            // Check if lexer grammar.
            AntlrGrammarDetails pd_parser = ParserDetailsFactory.Create(document) as AntlrGrammarDetails;
            ExtractGrammarType lp = new ExtractGrammarType();
            ParseTreeWalker.Default.Walk(lp, pd_parser.ParseTree);
            bool is_lexer = lp.Type == ExtractGrammarType.GrammarType.Lexer;
            if (is_lexer)
            {
                return result;
            }

            TableOfRules table = new TableOfRules(pd_parser, document);
            table.ReadRules();
            table.FindPartitions();
            table.FindStartRules();

            // Find new order of rules.
            string old_code = document.Code;
            List<Pair<int, int>> reorder = new List<Pair<int, int>>();
            if (type == LspAntlr.ReorderType.DFS)
            {
                Digraph<string> graph = new Digraph<string>();
                foreach (TableOfRules.Row r in table.rules)
                {
                    if (!r.is_parser_rule)
                    {
                        continue;
                    }

                    graph.AddVertex(r.LHS);
                }
                foreach (TableOfRules.Row r in table.rules)
                {
                    if (!r.is_parser_rule)
                    {
                        continue;
                    }

                    List<string> j = r.RHS;
                    //j.Reverse();
                    foreach (string rhs in j)
                    {
                        TableOfRules.Row sym = table.rules.Where(t => t.LHS == rhs).FirstOrDefault();
                        if (!sym.is_parser_rule)
                        {
                            continue;
                        }

                        DirectedEdge<string> e = new DirectedEdge<string>(r.LHS, rhs);
                        graph.AddEdge(e);
                    }
                }
                List<string> starts = new List<string>();
                foreach (TableOfRules.Row r in table.rules)
                {
                    if (r.is_parser_rule && r.is_start)
                    {
                        starts.Add(r.LHS);
                    }
                }
                Algorithms.DepthFirstOrder<string, DirectedEdge<string>> sort = new DepthFirstOrder<string, DirectedEdge<string>>(graph, starts);
                List<string> ordered = sort.ToList();
                foreach (string s in ordered)
                {
                    TableOfRules.Row row = table.rules[table.nt_to_index[s]];
                    reorder.Add(new Pair<int, int>(row.start_index, row.end_index));
                }
            }
            else if (type == LspAntlr.ReorderType.BFS)
            {
                Digraph<string> graph = new Digraph<string>();
                foreach (TableOfRules.Row r in table.rules)
                {
                    if (!r.is_parser_rule)
                    {
                        continue;
                    }

                    graph.AddVertex(r.LHS);
                }
                foreach (TableOfRules.Row r in table.rules)
                {
                    if (!r.is_parser_rule)
                    {
                        continue;
                    }

                    List<string> j = r.RHS;
                    //j.Reverse();
                    foreach (string rhs in j)
                    {
                        TableOfRules.Row sym = table.rules.Where(t => t.LHS == rhs).FirstOrDefault();
                        if (!sym.is_parser_rule)
                        {
                            continue;
                        }

                        DirectedEdge<string> e = new DirectedEdge<string>(r.LHS, rhs);
                        graph.AddEdge(e);
                    }
                }
                List<string> starts = new List<string>();
                foreach (TableOfRules.Row r in table.rules)
                {
                    if (r.is_parser_rule && r.is_start)
                    {
                        starts.Add(r.LHS);
                    }
                }
                Algorithms.BreadthFirstOrder<string, DirectedEdge<string>> sort = new BreadthFirstOrder<string, DirectedEdge<string>>(graph, starts);
                List<string> ordered = sort.ToList();
                foreach (string s in ordered)
                {
                    TableOfRules.Row row = table.rules[table.nt_to_index[s]];
                    reorder.Add(new Pair<int, int>(row.start_index, row.end_index));
                }
            }
            else if (type == LspAntlr.ReorderType.Alphabetically)
            {
                List<string> ordered = table.rules
                    .Where(r => r.is_parser_rule)
                    .Select(r => r.LHS)
                    .OrderBy(r => r).ToList();
                foreach (string s in ordered)
                {
                    TableOfRules.Row row = table.rules[table.nt_to_index[s]];
                    reorder.Add(new Pair<int, int>(row.start_index, row.end_index));
                }
            }
            else
            {
                return result;
            }

            StringBuilder sb = new StringBuilder();
            int previous = 0;
            {
                int index_start = table.rules[0].start_index;
                int len = 0;
                string pre = old_code.Substring(previous, index_start - previous);
                sb.Append(pre);
                previous = index_start + len;
            }
            foreach (Pair<int, int> l in reorder)
            {
                int index_start = l.a;
                int len = l.b - l.a;
                string add = old_code.Substring(index_start, len);
                sb.Append(add);
            }
            // Now add all non-parser rules.
            foreach (TableOfRules.Row r in table.rules)
            {
                if (r.is_parser_rule)
                {
                    continue;
                }

                int index_start = r.start_index;
                int len = r.end_index - r.start_index;
                string add = old_code.Substring(index_start, len);
                sb.Append(add);
            }
            //string rest = old_code.Substring(previous);
            //sb.Append(rest);
            string new_code = sb.ToString();
            result.Add(document.FullPath, new_code);

            return result;
        }

        public static Dictionary<string, string> SplitCombineGrammars(int pos, Document document, bool split)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();

            // Check if lexer grammar.
            AntlrGrammarDetails pd_parser = ParserDetailsFactory.Create(document) as AntlrGrammarDetails;
            ExtractGrammarType lp = new ExtractGrammarType();
            ParseTreeWalker.Default.Walk(lp, pd_parser.ParseTree);
            if (split && lp.Type != ExtractGrammarType.GrammarType.Combined)
            {
                return null;
            }
            if ((!split) && lp.Type != ExtractGrammarType.GrammarType.Parser)
            {
                return null;
            }

            TableOfRules table = new TableOfRules(pd_parser, document);
            table.ReadRules();
            table.FindPartitions();
            table.FindStartRules();

            string old_code = document.Code;
            if (split)
            {
                // Create a parser and lexer grammar.
                StringBuilder sb_parser = new StringBuilder();
                StringBuilder sb_lexer = new StringBuilder();
                ANTLRv4Parser.GrammarSpecContext root = pd_parser.ParseTree as ANTLRv4Parser.GrammarSpecContext;
                if (root == null)
                {
                    return null;
                }

                int grammar_type_index = 0;
                if (root.DOC_COMMENT() != null)
                {
                    grammar_type_index++;
                }

                ANTLRv4Parser.GrammarDeclContext grammar_type_tree = root.grammarDecl();
                ANTLRv4Parser.IdentifierContext id = grammar_type_tree.identifier();
                ITerminalNode semi_tree = grammar_type_tree.SEMI();
                ANTLRv4Parser.RulesContext rules_tree = root.rules();
                string pre = old_code.Substring(0, pd_parser.TokStream.Get(grammar_type_tree.SourceInterval.a).StartIndex - 0);
                sb_parser.Append(pre);
                sb_lexer.Append(pre);
                sb_parser.Append("parser grammar " + id.GetText() + "Parser;" + Environment.NewLine);
                sb_lexer.Append("lexer grammar " + id.GetText() + "Lexer;" + Environment.NewLine);
                int x1 = pd_parser.TokStream.Get(semi_tree.SourceInterval.b).StopIndex + 1;
                int x2 = pd_parser.TokStream.Get(rules_tree.SourceInterval.a).StartIndex;
                string n1 = old_code.Substring(x1, x2 - x1);
                sb_parser.Append(n1);
                sb_lexer.Append(n1);
                sb_parser.AppendLine("options { tokenVocab=" + id.GetText() + "Lexer; }");
                int end = 0;
                for (int i = 0; i < table.rules.Count; ++i)
                {
                    TableOfRules.Row r = table.rules[i];
                    // Partition rule symbols.
                    if (r.is_parser_rule)
                    {
                        string n2 = old_code.Substring(r.start_index, r.end_index - r.start_index);
                        sb_parser.Append(n2);
                    }
                    else
                    {
                        string n2 = old_code.Substring(r.start_index, r.end_index - r.start_index);
                        sb_lexer.Append(n2);
                    }
                    end = r.end_index + 1;
                }
                if (end < old_code.Length)
                {
                    string rest = old_code.Substring(end);
                    sb_parser.Append(rest);
                    sb_lexer.Append(rest);
                }
                string g4_file_path = document.FullPath;
                string current_dir = Path.GetDirectoryName(g4_file_path);
                if (current_dir == null)
                {
                    return null;
                }
                string orig_name = Path.GetFileNameWithoutExtension(g4_file_path);
                string new_code_parser = sb_parser.ToString();
                string new_parser_ffn = current_dir + Path.DirectorySeparatorChar
                    + orig_name + "Parser.g4";
                string new_lexer_ffn = current_dir + Path.DirectorySeparatorChar
                    + orig_name + "Lexer.g4";
                string new_code_lexer = sb_lexer.ToString();
                result.Add(new_parser_ffn, new_code_parser);
                result.Add(new_lexer_ffn, new_code_lexer);
                result.Add(g4_file_path, null);
            }
            else
            {
                // Parse grammar.
                HashSet<string> read_files = new HashSet<string>
                {
                    document.FullPath
                };
                for (; ; )
                {
                    int before_count = read_files.Count;
                    foreach (string f in read_files)
                    {
                        List<string> additional = AntlrGrammarDetails._dependent_grammars.Where(
                            t => t.Value.Contains(f)).Select(
                            t => t.Key).ToList();
                        read_files = read_files.Union(additional).ToHashSet();
                    }
                    int after_count = read_files.Count;
                    if (after_count == before_count)
                    {
                        break;
                    }
                }
                List<AntlrGrammarDetails> grammars = new List<AntlrGrammarDetails>();
                foreach (string f in read_files)
                {
                    Workspaces.Document d = Workspaces.Workspace.Instance.FindDocument(f);
                    if (d == null)
                    {
                        continue;
                    }
                    AntlrGrammarDetails x = ParserDetailsFactory.Create(d) as AntlrGrammarDetails;
                    grammars.Add(x);
                }

                // I'm going to have to assume two grammars, one lexer and one parser grammar each.
                if (grammars.Count != 2)
                {
                    return null;
                }

                // Read now lexer grammar. The parser grammar was already read.
                AntlrGrammarDetails pd_lexer = grammars[1];
                Workspaces.Document ldocument = Workspaces.Workspace.Instance.FindDocument(pd_lexer.FullFileName);
                TableOfRules lexer_table = new TableOfRules(pd_lexer, ldocument);
                lexer_table.ReadRules();
                lexer_table.FindPartitions();
                lexer_table.FindStartRules();

                // Look for tokenVocab.
                FindOptions find_options = new FindOptions();
                ParseTreeWalker.Default.Walk(find_options, pd_parser.ParseTree);
                ANTLRv4Parser.OptionContext tokenVocab = null;
                foreach (var o in find_options.Options)
                {
                    var oo = o as ANTLRv4Parser.OptionContext;
                    if (oo.identifier() != null && oo.identifier().GetText() == "tokenVocab")
                    {
                        tokenVocab = oo;
                    }
                }
                bool remove_options_spec = tokenVocab != null && find_options.Options.Count == 1;
                bool rewrite_options_spec = tokenVocab != null;

                // Create a combined parser grammar.
                StringBuilder sb_parser = new StringBuilder();
                ANTLRv4Parser.GrammarSpecContext root = pd_parser.ParseTree as ANTLRv4Parser.GrammarSpecContext;
                if (root == null)
                {
                    return null;
                }

                int grammar_type_index = 0;
                if (root.DOC_COMMENT() != null)
                {
                    grammar_type_index++;
                }

                ANTLRv4Parser.GrammarDeclContext grammar_type_tree = root.grammarDecl();
                ANTLRv4Parser.IdentifierContext id = grammar_type_tree.identifier();
                ITerminalNode semi_tree = grammar_type_tree.SEMI();
                ANTLRv4Parser.RulesContext rules_tree = root.rules();
                string pre = old_code.Substring(0, pd_parser.TokStream.Get(grammar_type_tree.SourceInterval.a).StartIndex - 0);
                sb_parser.Append(pre);
                sb_parser.Append("grammar " + id.GetText().Replace("Parser", "") + ";" + Environment.NewLine);

                if (!(remove_options_spec || rewrite_options_spec))
                {
                    int x1 = pd_parser.TokStream.Get(semi_tree.SourceInterval.b).StopIndex + 1;
                    int x2 = pd_parser.TokStream.Get(rules_tree.SourceInterval.a).StartIndex;
                    string n1 = old_code.Substring(x1, x2 - x1);
                    sb_parser.Append(n1);
                }
                else if (remove_options_spec)
                {
                    int x1 = pd_parser.TokStream.Get(semi_tree.SourceInterval.b).StopIndex + 1;
                    int x2 = pd_parser.TokStream.Get(find_options.OptionsSpec.SourceInterval.a).StartIndex;
                    int x3 = pd_parser.TokStream.Get(find_options.OptionsSpec.SourceInterval.b).StopIndex + 1;
                    int x4 = pd_parser.TokStream.Get(rules_tree.SourceInterval.a).StartIndex;
                    string n1 = old_code.Substring(x1, x2 - x1);
                    sb_parser.Append(n1);
                    string n3 = old_code.Substring(x3, x4 - x3);
                    sb_parser.Append(n3);
                }
                else if (rewrite_options_spec)
                {
                    int x1 = pd_parser.TokStream.Get(semi_tree.SourceInterval.b).StopIndex + 1;
                    int x2 = 0;
                    int x3 = 0;
                    foreach (var o in find_options.Options)
                    {
                        var oo = o as ANTLRv4Parser.OptionContext;
                        if (oo.identifier() != null && oo.identifier().GetText() == "tokenVocab")
                        {
                            x2 = pd_parser.TokStream.Get(oo.SourceInterval.a).StartIndex;
                            int j;
                            for (j = oo.SourceInterval.b + 1; ; j++)
                            {
                                if (pd_parser.TokStream.Get(j).Text == ";")
                                {
                                    j++;
                                    break;
                                }
                            }
                            x3 = pd_parser.TokStream.Get(j).StopIndex + 1;
                            break;
                        }
                    }
                    int x4 = pd_parser.TokStream.Get(rules_tree.SourceInterval.a).StartIndex;
                    string n1 = old_code.Substring(x1, x2 - x1);
                    sb_parser.Append(n1);
                    string n2 = old_code.Substring(x2, x3 - x2);
                    sb_parser.Append(n2);
                    string n4 = old_code.Substring(x3, x4 - x3);
                    sb_parser.Append(n4);
                }
                int end = 0;
                for (int i = 0; i < table.rules.Count; ++i)
                {
                    TableOfRules.Row r = table.rules[i];
                    if (r.is_parser_rule)
                    {
                        string n2 = old_code.Substring(r.start_index, r.end_index - r.start_index);
                        sb_parser.Append(n2);
                    }
                    end = r.end_index + 1;
                }
                if (end < old_code.Length)
                {
                    string rest = old_code.Substring(end);
                    sb_parser.Append(rest);
                }
                end = 0;
                string lexer_old_code = ldocument.Code;
                for (int i = 0; i < lexer_table.rules.Count; ++i)
                {
                    TableOfRules.Row r = lexer_table.rules[i];
                    if (!r.is_parser_rule)
                    {
                        string n2 = lexer_old_code.Substring(r.start_index, r.end_index - r.start_index);
                        sb_parser.Append(n2);
                    }
                    end = r.end_index + 1;
                }
                if (end < lexer_old_code.Length)
                {
                    string rest = lexer_old_code.Substring(end);
                    sb_parser.Append(rest);
                }
                string g4_file_path = document.FullPath;
                string current_dir = Path.GetDirectoryName(g4_file_path);
                if (current_dir == null)
                {
                    return null;
                }

                string orig_name = Path.GetFileName(g4_file_path);
                string new_name = orig_name.Replace("Parser.g4", "");
                string new_code_parser = sb_parser.ToString();
                string new_parser_ffn = current_dir + Path.DirectorySeparatorChar
                    + new_name + ".g4";
                result.Add(new_parser_ffn, new_code_parser);
                result.Add(pd_parser.FullFileName, null);
                result.Add(pd_lexer.FullFileName, null);
            }

            return result;
        }


        private static bool HasDirectLeftRecursion(IParseTree rule)
        {
            if (!(rule is ANTLRv4Parser.ParserRuleSpecContext))
                return false;
            var r = rule as ANTLRv4Parser.ParserRuleSpecContext;
            var lhs = r.RULE_REF();
            var rb = r.ruleBlock();
            if (rb == null) return false;
            var ral = rb.ruleAltList();
            foreach (var la in ral.labeledAlt())
            {
                TerminalNodeImpl t1 = la
                    .alternative()?
                    .element()?
                    .FirstOrDefault()?
                    .atom()?
                    .ruleref()?
                    .GetChild(0) as TerminalNodeImpl;
                if (t1 != null && t1.GetText() == lhs.GetText())
                {
                    return true;
                }
                TerminalNodeImpl t2 = la
                    .alternative()?
                    .element()?
                    .FirstOrDefault()?
                    .labeledElement()?
                    .atom()?
                    .ruleref()?
                    .GetChild(0) as TerminalNodeImpl;
                if (t2 != null && t2.GetText() == lhs.GetText())
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasDirectRightRecursion(IParseTree rule)
        {
            if (!(rule is ANTLRv4Parser.ParserRuleSpecContext))
                return false;
            var r = rule as ANTLRv4Parser.ParserRuleSpecContext;
            var lhs = r.RULE_REF();
            var rb = r.ruleBlock();
            if (rb == null) return false;
            var ral = rb.ruleAltList();
            foreach (var la in ral.labeledAlt())
            {
                TerminalNodeImpl t1 = la
                    .alternative()?
                    .element()?
                    .LastOrDefault()?
                    .atom()?
                    .ruleref()?
                    .GetChild(0) as TerminalNodeImpl;
                if (t1 != null && t1.GetText() == lhs.GetText())
                {
                    return true;
                }
                TerminalNodeImpl t2 = la
                    .alternative()?
                    .element()?
                    .LastOrDefault()?
                    .labeledElement()?
                    .atom()?
                    .ruleref()?
                    .GetChild(0) as TerminalNodeImpl;
                if (t2 != null && t2.GetText() == lhs.GetText())
                {
                    return true;
                }
            }
            return false;
        }

        private static (IParseTree, IParseTree) GenerateReplacementRules(string new_symbol_name, IParseTree rule)
        {
            ANTLRv4Parser.ParserRuleSpecContext new_a_rule = new ANTLRv4Parser.ParserRuleSpecContext(null, 0);
            {
                var r = rule as ANTLRv4Parser.ParserRuleSpecContext;
                var lhs = r.RULE_REF()?.GetText();
                {
                    CopyTreeRecursive(r.RULE_REF(), new_a_rule);
                }
                // Now have "A"
                {
                    var token_type2 = r.COLON().Symbol.Type;
                    var token2 = new CommonToken(token_type2) { Line = 0, Column = 0, Text = ":" };
                    var new_colon = new TerminalNodeImpl(token2);
                    new_a_rule.AddChild(new_colon);
                    new_colon.Parent = new_a_rule;
                }
                // Now have "A :"
                ANTLRv4Parser.RuleAltListContext rule_alt_list = new ANTLRv4Parser.RuleAltListContext(null, 0);
                {
                    ANTLRv4Parser.RuleBlockContext new_rule_block_context = new ANTLRv4Parser.RuleBlockContext(new_a_rule, 0);
                    new_a_rule.AddChild(new_rule_block_context);
                    new_rule_block_context.Parent = new_a_rule;
                    new_rule_block_context.AddChild(rule_alt_list);
                    rule_alt_list.Parent = new_rule_block_context;
                }
                // Now have "A : <rb <ral> >"
                {
                    var token_type3 = r.SEMI().Symbol.Type;
                    var token3 = new CommonToken(token_type3) { Line = 0, Column = 0, Text = ";" };
                    var new_semi = new TerminalNodeImpl(token3);
                    new_a_rule.AddChild(new_semi);
                    new_semi.Parent = new_a_rule;
                }
                // Now have "A : <rb <ral> > ;"
                {
                    CopyTreeRecursive(r.exceptionGroup(), new_a_rule);
                }
                // Now have "A : <rb <ral> > ; <eg>"
                bool first = true;
                foreach (ANTLRv4Parser.AlternativeContext alt in EnumeratorOfAlts(rule))
                {
                    ANTLRv4Parser.AtomContext atom = EnumeratorOfRHS(alt)?.FirstOrDefault();
                    if (lhs == atom?.GetText())
                    {
                        // skip alts that have direct left recursion.
                        continue;
                    }
                    {
                        if (!first)
                        {
                            var token_type4 = ANTLRv4Lexer.OR;
                            var token4 = new CommonToken(token_type4) { Line = 0, Column = 0, Text = "|" };
                            var new_or = new TerminalNodeImpl(token4);
                            rule_alt_list.AddChild(new_or);
                            new_or.Parent = rule_alt_list;
                        }
                        first = false;
                    }
                    ANTLRv4Parser.LabeledAltContext l_alt = new ANTLRv4Parser.LabeledAltContext(rule_alt_list, 0);
                    rule_alt_list.AddChild(l_alt);
                    l_alt.Parent = rule_alt_list;
                    // Create new alt "beta A'".
                    ANTLRv4Parser.AlternativeContext new_alt = new ANTLRv4Parser.AlternativeContext(null, 0);
                    l_alt.AddChild(new_alt);
                    new_alt.Parent = l_alt;
                    foreach (var element in alt.element())
                    {
                        CopyTreeRecursive(element, new_alt);
                    }
                    var token_type = ANTLRv4Lexer.RULE_REF;
                    var token = new CommonToken(token_type) { Line = 0, Column = 0, Text = new_symbol_name };
                    var new_rule_ref = new TerminalNodeImpl(token);
                    var new_ruleref = new ANTLRv4Parser.RulerefContext(null, 0);
                    new_ruleref.AddChild(new_rule_ref);
                    new_rule_ref.Parent = new_ruleref;
                    var new_atom = new ANTLRv4Parser.AtomContext(null, 0);
                    new_atom.AddChild(new_ruleref);
                    new_ruleref.Parent = new_atom;
                    var new_element = new ANTLRv4Parser.ElementContext(null, 0);
                    new_element.AddChild(new_atom);
                    new_atom.Parent = new_element;
                    new_alt.AddChild(new_element);
                    new_element.Parent = new_alt;
                }
            }
            // Now have "A : beta1 A' | beta2 A' | ... ; <eg>"

            ANTLRv4Parser.ParserRuleSpecContext new_ap_rule = new ANTLRv4Parser.ParserRuleSpecContext(null, 0);
            {
                var r = rule as ANTLRv4Parser.ParserRuleSpecContext;
                var lhs = r.RULE_REF()?.GetText();
                {
                    var token_type = r.RULE_REF().Symbol.Type;
                    var token = new CommonToken(token_type) { Line = 0, Column = 0, Text = new_symbol_name };
                    var new_rule_ref = new TerminalNodeImpl(token);
                    new_ap_rule.AddChild(new_rule_ref);
                    new_rule_ref.Parent = new_ap_rule;
                }
                // Now have "A'"
                {
                    var token_type2 = r.COLON().Symbol.Type;
                    var token2 = new CommonToken(token_type2) { Line = 0, Column = 0, Text = ":" };
                    var new_colon = new TerminalNodeImpl(token2);
                    new_ap_rule.AddChild(new_colon);
                    new_colon.Parent = new_ap_rule;
                }
                // Now have "A' :"
                ANTLRv4Parser.RuleAltListContext rule_alt_list = new ANTLRv4Parser.RuleAltListContext(null, 0);
                {
                    ANTLRv4Parser.RuleBlockContext new_rule_block_context = new ANTLRv4Parser.RuleBlockContext(new_a_rule, 0);
                    new_ap_rule.AddChild(new_rule_block_context);
                    new_rule_block_context.Parent = new_ap_rule;
                    new_rule_block_context.AddChild(rule_alt_list);
                    rule_alt_list.Parent = new_rule_block_context;
                }
                // Now have "A' : <rb <ral> >"
                {
                    var token_type3 = r.SEMI().Symbol.Type;
                    var token3 = new CommonToken(token_type3) { Line = 0, Column = 0, Text = ";" };
                    var new_semi = new TerminalNodeImpl(token3);
                    new_ap_rule.AddChild(new_semi);
                    new_semi.Parent = new_ap_rule;
                }
                // Now have "A : <rb <ral> > ;"
                {
                    CopyTreeRecursive(r.exceptionGroup(), new_a_rule);
                }
                // Now have "A' : <rb <ral> > ; <eg>"
                bool first = true;
                foreach (ANTLRv4Parser.AlternativeContext alt in EnumeratorOfAlts(rule))
                {
                    ANTLRv4Parser.AtomContext atom = EnumeratorOfRHS(alt)?.FirstOrDefault();
                    if (lhs != atom?.GetText())
                    {
                        // skip alts that DO NOT have direct left recursion.
                        continue;
                    }
                    {
                        if (!first)
                        {
                            var token_type4 = ANTLRv4Lexer.OR;
                            var token4 = new CommonToken(token_type4) { Line = 0, Column = 0, Text = "|" };
                            var new_or = new TerminalNodeImpl(token4);
                            rule_alt_list.AddChild(new_or);
                            new_or.Parent = rule_alt_list;
                        }
                        first = false;
                    }
                    ANTLRv4Parser.LabeledAltContext l_alt = new ANTLRv4Parser.LabeledAltContext(rule_alt_list, 0);
                    rule_alt_list.AddChild(l_alt);
                    l_alt.Parent = rule_alt_list;
                    // Create new alt "alpha A'".
                    ANTLRv4Parser.AlternativeContext new_alt = new ANTLRv4Parser.AlternativeContext(null, 0);
                    l_alt.AddChild(new_alt);
                    new_alt.Parent = l_alt;
                    bool first2 = true;
                    foreach (var element in alt.element())
                    {
                        if (first2)
                        {
                            first2 = false;
                            continue;
                        }
                        CopyTreeRecursive(element, new_alt);
                    }
                    var token_type = r.RULE_REF().Symbol.Type;
                    var token = new CommonToken(token_type) { Line = 0, Column = 0, Text = new_symbol_name };
                    var new_rule_ref = new TerminalNodeImpl(token);
                    var new_ruleref = new ANTLRv4Parser.RulerefContext(null, 0);
                    new_ruleref.AddChild(new_rule_ref);
                    new_rule_ref.Parent = new_ruleref;
                    var new_atom = new ANTLRv4Parser.AtomContext(null, 0);
                    new_atom.AddChild(new_ruleref);
                    new_ruleref.Parent = new_atom;
                    var new_element = new ANTLRv4Parser.ElementContext(null, 0);
                    new_element.AddChild(new_atom);
                    new_atom.Parent = new_element;
                    new_alt.AddChild(new_element);
                    new_element.Parent = new_alt;
                }
                {
                    if (!first)
                    {
                        var token_type4 = ANTLRv4Lexer.OR;
                        var token4 = new CommonToken(token_type4) { Line = 0, Column = 0, Text = "|" };
                        var new_or = new TerminalNodeImpl(token4);
                        rule_alt_list.AddChild(new_or);
                        new_or.Parent = rule_alt_list;
                    }
                    first = false;
                    ANTLRv4Parser.LabeledAltContext l_alt = new ANTLRv4Parser.LabeledAltContext(rule_alt_list, 0);
                    rule_alt_list.AddChild(l_alt);
                    l_alt.Parent = rule_alt_list;
                    // Create new empty alt.
                    ANTLRv4Parser.AlternativeContext new_alt = new ANTLRv4Parser.AlternativeContext(null, 0);
                    l_alt.AddChild(new_alt);
                    new_alt.Parent = l_alt;
                }
            }
            // Now have "A' : alpha1 A' | alpha2 A' | ... ;"

            return ((IParseTree)new_a_rule, (IParseTree)new_ap_rule);
        }

        private static IParseTree ReplaceWithKleeneRules(bool has_direct_left_recursion, bool has_direct_right_recursion, IParseTree rule)
        {
            // Convert A -> A beta1 | A beta2 | ... | alpha1 | alpha2 | ... ;
            // into A ->  (alpha1 | alpha2 | ... ) (beta1 | beta2 | ...)*

            ANTLRv4Parser.ParserRuleSpecContext new_a_rule = new ANTLRv4Parser.ParserRuleSpecContext(null, 0);
            {
                var r = rule as ANTLRv4Parser.ParserRuleSpecContext;
                var lhs = r.RULE_REF()?.GetText();
                {
                    CopyTreeRecursive(r.RULE_REF(), new_a_rule);
                }
                // Now have "A"
                {
                    var token_type2 = r.COLON().Symbol.Type;
                    var token2 = new CommonToken(token_type2) { Line = 0, Column = 0, Text = ":" };
                    var new_colon = new TerminalNodeImpl(token2);
                    new_a_rule.AddChild(new_colon);
                    new_colon.Parent = new_a_rule;
                }
                // Now have "A :"
                ANTLRv4Parser.RuleAltListContext rule_alt_list = new ANTLRv4Parser.RuleAltListContext(null, 0);
                ANTLRv4Parser.AltListContext altlist1 = new ANTLRv4Parser.AltListContext(null, 0);
                ANTLRv4Parser.AltListContext altlist2 = new ANTLRv4Parser.AltListContext(null, 0);
                {
                    ANTLRv4Parser.RuleBlockContext new_rule_block_context = new ANTLRv4Parser.RuleBlockContext(new_a_rule, 0);
                    new_a_rule.AddChild(new_rule_block_context);
                    new_rule_block_context.Parent = new_a_rule;
                    new_rule_block_context.AddChild(rule_alt_list);
                    rule_alt_list.Parent = new_rule_block_context;
                    ANTLRv4Parser.LabeledAltContext l_alt = new ANTLRv4Parser.LabeledAltContext(null, 0);
                    rule_alt_list.AddChild(l_alt);
                    l_alt.Parent = rule_alt_list;
                    ANTLRv4Parser.AlternativeContext new_alt = new ANTLRv4Parser.AlternativeContext(null, 0);
                    l_alt.AddChild(new_alt);
                    new_alt.Parent = l_alt;
                    {
                        var new_element = new ANTLRv4Parser.ElementContext(null, 0);
                        new_alt.AddChild(new_element);
                        new_element.Parent = new_alt;
                        var new_ebnf = new ANTLRv4Parser.EbnfContext(null, 0);
                        new_element.AddChild(new_ebnf);
                        new_ebnf.Parent = new_element;
                        var new_block = new ANTLRv4Parser.BlockContext(null, 0);
                        new_ebnf.AddChild(new_block);
                        new_block.Parent = new_ebnf;
                        var lparen_token_type = ANTLRv4Lexer.LPAREN;
                        var lparen_token = new CommonToken(lparen_token_type) { Line = 0, Column = 0, Text = "(" };
                        var new_lparen = new TerminalNodeImpl(lparen_token);
                        new_block.AddChild(new_lparen);
                        new_lparen.Parent = new_block;
                        new_block.AddChild(altlist1);
                        altlist1.Parent = new_block;
                        var rparen_token_type = ANTLRv4Lexer.RPAREN;
                        var rparen_token = new CommonToken(rparen_token_type) { Line = 0, Column = 0, Text = ")" };
                        var new_rparen = new TerminalNodeImpl(rparen_token);
                        new_block.AddChild(new_rparen);
                        new_rparen.Parent = new_block;
                    }
                    {
                        var new_element = new ANTLRv4Parser.ElementContext(null, 0);
                        new_alt.AddChild(new_element);
                        new_element.Parent = new_alt;
                        var new_ebnf = new ANTLRv4Parser.EbnfContext(null, 0);
                        new_element.AddChild(new_ebnf);
                        new_ebnf.Parent = new_element;
                        var new_block = new ANTLRv4Parser.BlockContext(null, 0);
                        new_ebnf.AddChild(new_block);
                        new_block.Parent = new_ebnf;
                        var new_blocksuffix = new ANTLRv4Parser.BlockSuffixContext(null, 0);
                        new_ebnf.AddChild(new_blocksuffix);
                        new_blocksuffix.Parent = new_ebnf;
                        var new_ebnfsuffix = new ANTLRv4Parser.EbnfSuffixContext(null, 0);
                        new_blocksuffix.AddChild(new_ebnfsuffix);
                        new_ebnfsuffix.Parent = new_blocksuffix;
                        var star_token_type = ANTLRv4Lexer.STAR;
                        var star_token = new CommonToken(star_token_type) { Line = 0, Column = 0, Text = "*" };
                        var new_star = new TerminalNodeImpl(star_token);
                        new_ebnfsuffix.AddChild(new_star);
                        new_star.Parent = new_ebnfsuffix;
                        var lparen_token_type = ANTLRv4Lexer.LPAREN;
                        var lparen_token = new CommonToken(lparen_token_type) { Line = 0, Column = 0, Text = "(" };
                        var new_lparen = new TerminalNodeImpl(lparen_token);
                        new_block.AddChild(new_lparen);
                        new_lparen.Parent = new_block;
                        new_block.AddChild(altlist2);
                        altlist2.Parent = new_block;
                        var rparen_token_type = ANTLRv4Lexer.RPAREN;
                        var rparen_token = new CommonToken(rparen_token_type) { Line = 0, Column = 0, Text = ")" };
                        var new_rparen = new TerminalNodeImpl(rparen_token);
                        new_block.AddChild(new_rparen);
                        new_rparen.Parent = new_block;
                    }
                }
                {
                    var token_type3 = r.SEMI().Symbol.Type;
                    var token3 = new CommonToken(token_type3) { Line = 0, Column = 0, Text = ";" };
                    var new_semi = new TerminalNodeImpl(token3);
                    new_a_rule.AddChild(new_semi);
                    new_semi.Parent = new_a_rule;
                }
                {
                    CopyTreeRecursive(r.exceptionGroup(), new_a_rule);
                }
                // Now have "A : <ruleBlock
                //                  <ruleAltList
                //                     <labeledAlt
                //                        <alternative
                //                           <element
                //                              <ebnf
                //                                 <block
                //                                    '('
                //                                       <altList1>
                //                                    ')'
                //                           >  >  >
                //                           <element
                //                              <ebnf
                //                                 <block
                //                                    '('
                //                                       <altList2>
                //                                    ')'
                //                                 >
                //                                 <blockSuffix
                //                                    <ebnfSuffix
                //                                       STAR
                //               >  >  >  >  >  >  >  >  ;  <eg>"
                if (has_direct_left_recursion && ! has_direct_right_recursion)
                {
                    bool first1 = true;
                    bool first2 = true;
                    foreach (ANTLRv4Parser.AlternativeContext alt in EnumeratorOfAlts(rule))
                    {
                        ANTLRv4Parser.AtomContext atom = EnumeratorOfRHS(alt)?.FirstOrDefault();
                        if (lhs == atom?.GetText())
                        {
                            if (!first1)
                            {
                                var token_type4 = ANTLRv4Lexer.OR;
                                var token4 = new CommonToken(token_type4) { Line = 0, Column = 0, Text = "|" };
                                var new_or = new TerminalNodeImpl(token4);
                                altlist2.AddChild(new_or);
                                new_or.Parent = altlist2;
                            }
                            first1 = false;
                            ANTLRv4Parser.LabeledAltContext l_alt = new ANTLRv4Parser.LabeledAltContext(null, 0);
                            altlist2.AddChild(l_alt);
                            l_alt.Parent = altlist2;
                            ANTLRv4Parser.AlternativeContext new_alt = new ANTLRv4Parser.AlternativeContext(null, 0);
                            l_alt.AddChild(new_alt);
                            new_alt.Parent = l_alt;
                            bool firsta = true;
                            foreach (var element in alt.element())
                            {
                                if (firsta)
                                {
                                    firsta = false;
                                    continue;
                                }
                                CopyTreeRecursive(element, new_alt);
                            }
                        }
                        else
                        {
                            if (!first2)
                            {
                                var token_type4 = ANTLRv4Lexer.OR;
                                var token4 = new CommonToken(token_type4) { Line = 0, Column = 0, Text = "|" };
                                var new_or = new TerminalNodeImpl(token4);
                                altlist1.AddChild(new_or);
                                new_or.Parent = altlist1;
                            }
                            first2 = false;
                            ANTLRv4Parser.LabeledAltContext l_alt = new ANTLRv4Parser.LabeledAltContext(null, 0);
                            altlist1.AddChild(l_alt);
                            l_alt.Parent = altlist1;
                            ANTLRv4Parser.AlternativeContext new_alt = new ANTLRv4Parser.AlternativeContext(null, 0);
                            l_alt.AddChild(new_alt);
                            new_alt.Parent = l_alt;
                            foreach (var element in alt.element())
                            {
                                CopyTreeRecursive(element, new_alt);
                            }
                        }
                    }
                }
            }
            return (IParseTree)new_a_rule;
        }

        private static string ComputeReplacementRules(string new_symbol_name, IParseTree rule)
        {
            if (rule is ANTLRv4Parser.ParserRuleSpecContext)
            {
                var r = rule as ANTLRv4Parser.ParserRuleSpecContext;
                var lhs = r.RULE_REF();
                var rb = r.ruleBlock();
                var rule_alt_list = rb.ruleAltList();

                StringBuilder sub1 = new StringBuilder();

                sub1.AppendLine(lhs.ToString());
                ANTLRv4Parser.LabeledAltContext[] alts = rule_alt_list.labeledAlt();
                bool first = true;
                for (int j = 0; j < alts.Length; ++j)
                {
                    var labeled_alt = alts[j];
                    ANTLRv4Parser.ElementContext[] elements = labeled_alt
                        .alternative()?
                        .element();
                    if (elements == null || elements.Length == 0)
                    {
                        continue;
                    }
                    var element = elements[0];
                    var rule_ref = element.atom()?.ruleref()?.RULE_REF();
                    if (rule_ref == null || rule_ref.GetText() != lhs.GetText())
                    {
                        if (first)
                            sub1.Append(" :");
                        else
                            sub1.Append(" |");
                        first = false;
                        for (int i = 0; i < elements.Length; ++i)
                        {
                            sub1.Append(" " + elements[i].GetText());
                        }
                        sub1.AppendLine(" " + new_symbol_name);
                    }
                }
                sub1.AppendLine(" ;");
                sub1.AppendLine();
                sub1.AppendLine(new_symbol_name);
                first = true;
                for (int j = 0; j < alts.Length; ++j)
                {
                    var labeled_alt = alts[j];
                    ANTLRv4Parser.ElementContext[] elements = labeled_alt
                        .alternative()?
                        .element();
                    if (elements == null || elements.Length == 0)
                    {
                        continue;
                    }
                    var element = elements[0];
                    var rule_ref = element.atom()?.ruleref()?.RULE_REF();
                    if (rule_ref != null && rule_ref.GetText() == lhs.GetText())
                    {
                        if (first)
                            sub1.Append(" :");
                        else
                            sub1.Append(" |");
                        first = false;
                        for (int i = 1; i < elements.Length; ++i)
                        {
                            sub1.Append(" " + elements[i].GetText());
                        }
                        sub1.AppendLine(" " + new_symbol_name);
                    }
                }
                sub1.AppendLine(" |");
                sub1.AppendLine(" ;");
                sub1.AppendLine();
                return sub1.ToString();
            }
            return null;
        }

        public static Dictionary<string, string> EliminateDirectLeftRecursion(int index, Document document)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();

            // Check if initial file is a grammar.
            AntlrGrammarDetails pd_parser = ParserDetailsFactory.Create(document) as AntlrGrammarDetails;
            ExtractGrammarType egt = new ExtractGrammarType();
            ParseTreeWalker.Default.Walk(egt, pd_parser.ParseTree);
            bool is_grammar = egt.Type == ExtractGrammarType.GrammarType.Parser
                || egt.Type == ExtractGrammarType.GrammarType.Combined
                || egt.Type == ExtractGrammarType.GrammarType.Lexer;
            if (!is_grammar)
            {
                return result;
            }

            // Find all other grammars by walking dependencies (import, vocab, file names).
            HashSet<string> read_files = new HashSet<string>
            {
                document.FullPath
            };
            Dictionary<Workspaces.Document, List<TerminalNodeImpl>> every_damn_literal =
                new Dictionary<Workspaces.Document, List<TerminalNodeImpl>>();
            for (; ; )
            {
                int before_count = read_files.Count;
                foreach (string f in read_files)
                {
                    List<string> additional = AntlrGrammarDetails._dependent_grammars.Where(
                        t => t.Value.Contains(f)).Select(
                        t => t.Key).ToList();
                    read_files = read_files.Union(additional).ToHashSet();
                }
                foreach (string f in read_files)
                {
                    IEnumerable<List<string>> additional = AntlrGrammarDetails._dependent_grammars.Where(
                        t => t.Key == f).Select(
                        t => t.Value);
                    foreach (List<string> t in additional)
                    {
                        read_files = read_files.Union(t).ToHashSet();
                    }
                }
                int after_count = read_files.Count;
                if (after_count == before_count)
                {
                    break;
                }
            }

            // Assume cursor positioned at the rule that contains left recursion.
            // Find rule.
            IParseTree rule = null;
            IParseTree it = pd_parser.AllNodes.Where(n =>
            {
                if (!(n is ANTLRv4Parser.ParserRuleSpecContext || n is ANTLRv4Parser.LexerRuleSpecContext))
                    return false;
                Interval source_interval = n.SourceInterval;
                int a = source_interval.a;
                int b = source_interval.b;
                IToken ta = pd_parser.TokStream.Get(a);
                IToken tb = pd_parser.TokStream.Get(b);
                var start = ta.StartIndex;
                var stop = tb.StopIndex + 1;
                return start <= index && index < stop;
            }).FirstOrDefault();
            if (it == null)
            {
                return result;
            }
            rule = it;

            // We are now at the rule that the user identified to eliminate direct
            // left recursion.
            // Check if the rule has direct left recursion.

            bool has_direct_left_recursion = HasDirectLeftRecursion(rule);
            if (!has_direct_left_recursion)
            {
                return result;
            }

            // Has direct left recursion.

            // Replace rule with two new rules.
            //
            // Original rule:
            // A
            //   : A a1
            //   | A a2
            //   | A a3
            //   | B1
            //   | B2
            //   ...
            //   ;
            // Note a1, a2, a3 ... cannot be empty sequences.
            // B1, B2, ... cannot start with A.
            //
            // New rules.
            //
            // A
            //   : B1 A'
            //   | B2 A'
            //   | ...
            //   ;
            // A'
            //   : a1 A'
            //   | a2 A'
            //   | ...
            //   | (empty)
            //   ;
            //

            string generated_name = GenerateNewName(rule, pd_parser);
            var replacement = ComputeReplacementRules(generated_name, rule);
            if (replacement == null)
            {
                return result;
            }

            StringBuilder sb = new StringBuilder();
            int pre = 0;
            Reconstruct(sb, pd_parser.TokStream, pd_parser.ParseTree, ref pre,
                x =>
                {
                    if (x == rule)
                    {
                        return replacement;
                    }
                    return null;
                });
            var new_code = sb.ToString();
            if (new_code != pd_parser.Code)
            {
                result.Add(document.FullPath, new_code);
            }

            return result;
        }

        public static Dictionary<string, string> ConvertRecursionToKleeneOperator(int index, Document document)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();

            // Check if initial file is a grammar.
            AntlrGrammarDetails pd_parser = ParserDetailsFactory.Create(document) as AntlrGrammarDetails;
            ExtractGrammarType egt = new ExtractGrammarType();
            ParseTreeWalker.Default.Walk(egt, pd_parser.ParseTree);
            bool is_grammar = egt.Type == ExtractGrammarType.GrammarType.Parser
                || egt.Type == ExtractGrammarType.GrammarType.Combined
                || egt.Type == ExtractGrammarType.GrammarType.Lexer;
            if (!is_grammar)
            {
                return result;
            }

            // Find all other grammars by walking dependencies (import, vocab, file names).
            HashSet<string> read_files = new HashSet<string>
            {
                document.FullPath
            };
            Dictionary<Workspaces.Document, List<TerminalNodeImpl>> every_damn_literal =
                new Dictionary<Workspaces.Document, List<TerminalNodeImpl>>();
            for (; ; )
            {
                int before_count = read_files.Count;
                foreach (string f in read_files)
                {
                    List<string> additional = AntlrGrammarDetails._dependent_grammars.Where(
                        t => t.Value.Contains(f)).Select(
                        t => t.Key).ToList();
                    read_files = read_files.Union(additional).ToHashSet();
                }
                foreach (string f in read_files)
                {
                    IEnumerable<List<string>> additional = AntlrGrammarDetails._dependent_grammars.Where(
                        t => t.Key == f).Select(
                        t => t.Value);
                    foreach (List<string> t in additional)
                    {
                        read_files = read_files.Union(t).ToHashSet();
                    }
                }
                int after_count = read_files.Count;
                if (after_count == before_count)
                {
                    break;
                }
            }

            // Assume cursor positioned at the rule that contains left recursion.
            // Find rule.
            IParseTree rule = null;
            IParseTree it = pd_parser.AllNodes.Where(n =>
            {
                if (!(n is ANTLRv4Parser.ParserRuleSpecContext || n is ANTLRv4Parser.LexerRuleSpecContext))
                    return false;
                Interval source_interval = n.SourceInterval;
                int a = source_interval.a;
                int b = source_interval.b;
                IToken ta = pd_parser.TokStream.Get(a);
                IToken tb = pd_parser.TokStream.Get(b);
                var start = ta.StartIndex;
                var stop = tb.StopIndex + 1;
                return start <= index && index < stop;
            }).FirstOrDefault();
            if (it == null)
            {
                return result;
            }
            rule = it;

            // We are now at the rule that the user identified to eliminate direct
            // left recursion.
            // Check if the rule has direct left recursion.

            bool has_direct_left_recursion = HasDirectLeftRecursion(rule);
            bool has_direct_right_recursion = HasDirectRightRecursion(rule);
            if (!(has_direct_left_recursion || has_direct_right_recursion))
            {
                return result;
            }

            // Has direct recursion.
            rule = ReplaceWithKleeneRules(has_direct_left_recursion, has_direct_right_recursion, rule);
            {
                // Now edit the file and return.
                StringBuilder sb = new StringBuilder();
                int pre = 0;
                Reconstruct(sb, pd_parser.TokStream, pd_parser.ParseTree, ref pre,
                    x =>
                    {
                        if (x is ANTLRv4Parser.ParserRuleSpecContext)
                        {
                            var y = x as ANTLRv4Parser.ParserRuleSpecContext;
                            var name = y.RULE_REF()?.GetText();
                            if (name == Lhs(rule).GetText())
                            {
                                StringBuilder sb2 = new StringBuilder();
                                Output(sb2, pd_parser.TokStream, rule);
                                return sb2.ToString();
                            }
                        }
                        return null;
                    });
                var new_code = sb.ToString();
                if (new_code != pd_parser.Code)
                {
                    result.Add(document.FullPath, new_code);
                }
            }

            return result;
        }

        private static string GenerateNewName(IParseTree rule, AntlrGrammarDetails pd_parser)
        {
            var r = rule as ANTLRv4Parser.ParserRuleSpecContext;
            if (r == null)
                return null;
            var b = r.RULE_REF().GetText();
            var list = pd_parser.AllNodes.Where(n =>
            {
                return (n is ANTLRv4Parser.ParserRuleSpecContext || n is ANTLRv4Parser.LexerRuleSpecContext);
            }).Select(n =>
            {
                if (n is ANTLRv4Parser.ParserRuleSpecContext)
                {
                    var z = n as ANTLRv4Parser.ParserRuleSpecContext;
                    var lhs = z.RULE_REF();
                    return lhs.GetText();
                }
                return "";
            }).ToList();
            int gnum = 1;
            for (; ; )
            {
                if (!list.Contains(b + gnum.ToString()))
                    break;
                gnum++;
            }
            return b + gnum.ToString();
        }

        public static Dictionary<string, string> EliminateIndirectLeftRecursion(int index, Document document)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();

            // Check if initial file is a grammar.
            AntlrGrammarDetails pd_parser = ParserDetailsFactory.Create(document) as AntlrGrammarDetails;
            ExtractGrammarType egt = new ExtractGrammarType();
            ParseTreeWalker.Default.Walk(egt, pd_parser.ParseTree);
            bool is_grammar = egt.Type == ExtractGrammarType.GrammarType.Parser
                || egt.Type == ExtractGrammarType.GrammarType.Combined
                || egt.Type == ExtractGrammarType.GrammarType.Lexer;
            if (!is_grammar)
            {
                return result;
            }

            // Find all other grammars by walking dependencies (import, vocab, file names).
            HashSet<string> read_files = new HashSet<string>
            {
                document.FullPath
            };
            Dictionary<Workspaces.Document, List<TerminalNodeImpl>> every_damn_literal =
                new Dictionary<Workspaces.Document, List<TerminalNodeImpl>>();
            for (; ; )
            {
                int before_count = read_files.Count;
                foreach (string f in read_files)
                {
                    List<string> additional = AntlrGrammarDetails._dependent_grammars.Where(
                        t => t.Value.Contains(f)).Select(
                        t => t.Key).ToList();
                    read_files = read_files.Union(additional).ToHashSet();
                }
                foreach (string f in read_files)
                {
                    IEnumerable<List<string>> additional = AntlrGrammarDetails._dependent_grammars.Where(
                        t => t.Key == f).Select(
                        t => t.Value);
                    foreach (List<string> t in additional)
                    {
                        read_files = read_files.Union(t).ToHashSet();
                    }
                }
                int after_count = read_files.Count;
                if (after_count == before_count)
                {
                    break;
                }
            }

            // Construct graph of symbol usage.
            TableOfRules table = new TableOfRules(pd_parser, document);
            table.ReadRules();
            table.FindPartitions();
            table.FindStartRules();
            Digraph<string> graph = new Digraph<string>();
            foreach (TableOfRules.Row r in table.rules)
            {
                if (!r.is_parser_rule)
                {
                    continue;
                }
                graph.AddVertex(r.LHS);
            }
            foreach (TableOfRules.Row r in table.rules)
            {
                if (!r.is_parser_rule)
                {
                    continue;
                }
                List<string> j = r.RHS;
                //j.Reverse();
                foreach (string rhs in j)
                {
                    TableOfRules.Row sym = table.rules.Where(t => t.LHS == rhs).FirstOrDefault();
                    if (!sym.is_parser_rule)
                    {
                        continue;
                    }
                    DirectedEdge<string> e = new DirectedEdge<string>(r.LHS, rhs);
                    graph.AddEdge(e);
                }
            }
            List<string> starts = new List<string>();
            List<string> parser_lhs_rules = new List<string>();
            foreach (TableOfRules.Row r in table.rules)
            {
                if (r.is_parser_rule)
                {
                    parser_lhs_rules.Add(r.LHS);
                    if (r.is_start)
                    {
                        starts.Add(r.LHS);
                    }
                }
            }
            var sort = new TopologicalSort<string, DirectedEdge<string>>(graph, starts);
            List<string> ordered = sort.ToList();
            if (ordered.Count != parser_lhs_rules.Count)
            {
                return result;
            }
            Dictionary<string, IParseTree> rules = new Dictionary<string, IParseTree>();
            foreach (string s in ordered)
            {
                var ai = table.rules.Where(r => r.LHS == s).First();
                var air = (ANTLRv4Parser.ParserRuleSpecContext)ai.rule;
                rules[s] = CopyTreeRecursive(air, null);
            }
            for (int i = 0; i < ordered.Count; ++i)
            {
                var ai = ordered[i];
				var ai_tree = rules[ai];
				ANTLRv4Parser.ParserRuleSpecContext new_a_rule = new ANTLRv4Parser.ParserRuleSpecContext(null, 0);
				var r = ai_tree as ANTLRv4Parser.ParserRuleSpecContext;
				var lhs = r.RULE_REF()?.GetText();
				CopyTreeRecursive(r.RULE_REF(), new_a_rule);
                // Now have "A"
                {
                    var token_type2 = r.COLON().Symbol.Type;
                    var token2 = new CommonToken(token_type2) { Line = 0, Column = 0, Text = ":" };
                    var new_colon = new TerminalNodeImpl(token2);
                    new_a_rule.AddChild(new_colon);
                    new_colon.Parent = new_a_rule;
                }
                // Now have "A :"
                ANTLRv4Parser.RuleAltListContext rule_alt_list = new ANTLRv4Parser.RuleAltListContext(null, 0);
                {
                    ANTLRv4Parser.RuleBlockContext new_rule_block_context = new ANTLRv4Parser.RuleBlockContext(new_a_rule, 0);
                    new_a_rule.AddChild(new_rule_block_context);
                    new_rule_block_context.Parent = new_a_rule;
                    new_rule_block_context.AddChild(rule_alt_list);
                    rule_alt_list.Parent = new_rule_block_context;
                }
                // Now have "A : <rb <ral> >"
                {
                    var token_type3 = r.SEMI().Symbol.Type;
                    var token3 = new CommonToken(token_type3) { Line = 0, Column = 0, Text = ";" };
                    var new_semi = new TerminalNodeImpl(token3);
                    new_a_rule.AddChild(new_semi);
                    new_semi.Parent = new_a_rule;
                }
                // Now have "A : <rb <ral> > ;"
                {
                    CopyTreeRecursive(r.exceptionGroup(), new_a_rule);
                }
                // Now have "A : <rb <ral> > ; <eg>"
                for (int j = 0; j < i; ++j)
                {
                    var aj = ordered[j];
                    var aj_tree = rules[aj];
                    var new_alts = new List<ANTLRv4Parser.AlternativeContext>();
                    foreach (ANTLRv4Parser.AlternativeContext alt in EnumeratorOfAlts(ai_tree))
                    {
                        ANTLRv4Parser.AtomContext atom = EnumeratorOfRHS(alt)?.FirstOrDefault();
                        if (aj != atom?.GetText())
                        {
                            // Leave alt unchanged.
                            new_alts.Add(alt);
                            continue;
                        }
                        
                        // Substitute Aj into Ai.
                        // Example:
                        // s : a A | B;
                        // a : a C | s D | ;
                        // ts order of symbols = [s, a].
                        // i = 1, j = 0.
                        // => a : a C | a A D | B D | ;

                        foreach (ANTLRv4Parser.AlternativeContext alt2 in EnumeratorOfAlts(aj_tree))
                        {
                            ANTLRv4Parser.AlternativeContext new_alt = new ANTLRv4Parser.AlternativeContext(null, 0);
                            foreach (var element in alt2.element())
                            {
                                CopyTreeRecursive(element, new_alt);
                            }
                            bool first = true;
                            foreach (var element in alt.element())
                            {
                                if (first)
                                {
                                    first = false;
                                    continue;
                                }
                                CopyTreeRecursive(element, new_alt);
                            }
                            new_alts.Add(new_alt);
                        }
                    }
                    {
                        bool first = true;
                        foreach (var new_alt in new_alts)
                        {
                            if (!first)
                            {
                                var token_type4 = ANTLRv4Lexer.OR;
                                var token4 = new CommonToken(token_type4) { Line = 0, Column = 0, Text = "|" };
                                var new_or = new TerminalNodeImpl(token4);
                                rule_alt_list.AddChild(new_or);
                                new_or.Parent = rule_alt_list;
                            }
                            first = false;
                            ANTLRv4Parser.LabeledAltContext l_alt = new ANTLRv4Parser.LabeledAltContext(rule_alt_list, 0);
                            rule_alt_list.AddChild(l_alt);
                            l_alt.Parent = rule_alt_list;
                            l_alt.AddChild(new_alt);
                            new_alt.Parent = l_alt;
                        }
                    }
                    rules[ai] = new_a_rule;
                }

                // Check if the rule ai has direct left recursion.
                bool has_direct_left_recursion = HasDirectLeftRecursion(rules[ai]);
                if (!has_direct_left_recursion)
                {
                    continue;
                }

                // Has direct left recursion.

                // Replace rule with two new rules.
                //
                // Original rule:
                // A
                //   : A a1
                //   | A a2
                //   | A a3
                //   | B1
                //   | B2
                //   ...
                //   ;
                // Note a1, a2, a3 ... cannot be empty sequences.
                // B1, B2, ... cannot start with A.
                //
                // New rules.
                //
                // A
                //   : B1 A'
                //   | B2 A'
                //   | ...
                //   ;
                // A'
                //   : a1 A'
                //   | a2 A'
                //   | ...
                //   | (empty)
                //   ;
                //

                string generated_name = GenerateNewName(rules[ai], pd_parser);
                var (fixed_rule, new_rule) = GenerateReplacementRules(generated_name, rules[ai]);
                rules[ai] = fixed_rule;
                rules[generated_name] = new_rule;
            }

            {
                // Now edit the file and return.
                StringBuilder sb = new StringBuilder();
                int pre = 0;
                Reconstruct(sb, pd_parser.TokStream, pd_parser.ParseTree, ref pre,
                    x =>
                    {
                        if (x is ANTLRv4Parser.ParserRuleSpecContext)
                        {
                            var y = x as ANTLRv4Parser.ParserRuleSpecContext;
                            var name = y.RULE_REF()?.GetText();
                            rules.TryGetValue(name, out IParseTree replacement);
                            if (replacement != null)
                            {
                                StringBuilder sb2 = new StringBuilder();
                                Output(sb2, pd_parser.TokStream, replacement);
                                foreach (var r in rules)
                                {
                                    var z = r.Key != name && r.Key.Contains(name);
                                    if (z)
                                    {
                                        sb2.AppendLine();
                                        Output(sb2, pd_parser.TokStream, r.Value);
                                    }
                                }
                                return sb2.ToString();
                            }
                        }
                        return null;
                    });
                var new_code = sb.ToString();
                if (new_code != pd_parser.Code)
                {
                    result.Add(document.FullPath, new_code);
                }

            }

            return result;
        }

        public static ITerminalNode Lhs(IParseTree ai)
        {
            var r = ai as ANTLRv4Parser.ParserRuleSpecContext;
            ITerminalNode lhs = r.RULE_REF();
            return lhs;
        }

        private static IParseTree CopyTreeRecursive(IParseTree original, IParseTree parent)
        {
            if (original == null) return null;
            if (original is ParserRuleContext)
            {
                var type = original.GetType();
                var new_node = (ParserRuleContext)Activator.CreateInstance(type, null, 0);
                if (parent != null)
                {
                    var parent_rule_context = (ParserRuleContext)parent;
                    new_node.Parent = parent_rule_context;
                    parent_rule_context.AddChild(new_node);
                }
                int child_count = original.ChildCount;
                for (int i = 0; i < child_count; ++i)
                {
                    var child = original.GetChild(i);
                    CopyTreeRecursive(child, new_node);
                }
                return new_node;
            }
            else if (original is TerminalNodeImpl)
            {
                var o = original as TerminalNodeImpl;
                var new_node = new TerminalNodeImpl(o.Symbol);
                if (parent != null)
                {
                    var parent_rule_context = (ParserRuleContext)parent;
                    new_node.Parent = parent_rule_context;
                    parent_rule_context.AddChild(new_node);
                }
                return new_node;
            }
            else return null;
        }

        public static IEnumerable<ANTLRv4Parser.AlternativeContext> EnumeratorOfAlts(IParseTree ai)
        {
            var r = ai as ANTLRv4Parser.ParserRuleSpecContext;
            ANTLRv4Parser.RuleBlockContext rhs = r.ruleBlock();
            ANTLRv4Parser.RuleAltListContext rule_alt_list = rhs.ruleAltList();
            foreach (ANTLRv4Parser.LabeledAltContext l_alt in rule_alt_list.labeledAlt())
            {
                ANTLRv4Parser.AlternativeContext alt = l_alt.alternative();
                yield return alt;
            }
        }

        public static IEnumerable<ANTLRv4Parser.AtomContext> EnumeratorOfRHS(ANTLRv4Parser.AlternativeContext alt)
        {
            foreach (ANTLRv4Parser.ElementContext element in alt.element())
            {
                ANTLRv4Parser.LabeledElementContext le = element.labeledElement();
                ANTLRv4Parser.AtomContext atom = element.atom();
                ANTLRv4Parser.EbnfContext ebnf = element.ebnf();
                if (le != null)
                {
                    yield return le.atom();
                }
                else if (atom != null)
                {
                    yield return atom;
                }
            }
        }


        public static string EliminateAntlrKeywordsInRules(Document document)
        {
            string result = null;

            // Check if initial file is a parser or combined grammar.
            AntlrGrammarDetails pd_parser = ParserDetailsFactory.Create(document) as AntlrGrammarDetails;
            ExtractGrammarType egt = new ExtractGrammarType();
            ParseTreeWalker.Default.Walk(egt, pd_parser.ParseTree);
            bool is_grammar = egt.Type == ExtractGrammarType.GrammarType.Parser
                || egt.Type == ExtractGrammarType.GrammarType.Combined
                ;
            if (!is_grammar)
            {
                return result;
            }

            TableOfRules table = new TableOfRules(pd_parser, document);
            table.ReadRules();
            table.FindPartitions();
            table.FindStartRules();

            StringBuilder sb = new StringBuilder();
            int pre = 0;
            Reconstruct(sb, pd_parser.TokStream, pd_parser.ParseTree, ref pre,
                n =>
                {
                    if (!(n is TerminalNodeImpl))
                    {
                        return null;
                    }
                    var t = n as TerminalNodeImpl;
                    var r = t.GetText();
                    if (t.Symbol.Type == ANTLRv4Lexer.RULE_REF)
                    {
                        if (r == "options"
                            || r == "grammar"
                            || r == "tokenVocab"
                            || r == "lexer"
                            || r == "parser"
                            || r == "rule")
                            return r + "_nonterminal";
                    }
                    return r;
                });
            return sb.ToString();
        }

        public static Dictionary<string, string> AddLexerRulesForStringLiterals(Document document)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();

            // Check if initial file is a grammar.
            AntlrGrammarDetails pd_parser = ParserDetailsFactory.Create(document) as AntlrGrammarDetails;
            ExtractGrammarType egt = new ExtractGrammarType();
            ParseTreeWalker.Default.Walk(egt, pd_parser.ParseTree);
            bool is_grammar = egt.Type == ExtractGrammarType.GrammarType.Parser
                || egt.Type == ExtractGrammarType.GrammarType.Combined
                ;
            if (!is_grammar)
            {
                return result;
            }

            // Find all other grammars by walking dependencies (import, vocab, file names).
            HashSet<string> read_files = new HashSet<string>
            {
                document.FullPath
            };
            Dictionary<Workspaces.Document, List<TerminalNodeImpl>> every_damn_literal =
                new Dictionary<Workspaces.Document, List<TerminalNodeImpl>>();
            for (; ; )
            {
                int before_count = read_files.Count;
                foreach (string f in read_files)
                {
                    List<string> additional = AntlrGrammarDetails._dependent_grammars.Where(
                        t => t.Value.Contains(f)).Select(
                        t => t.Key).ToList();
                    read_files = read_files.Union(additional).ToHashSet();
                }
                foreach (string f in read_files)
                {
                    IEnumerable<List<string>> additional = AntlrGrammarDetails._dependent_grammars.Where(
                        t => t.Key == f).Select(
                        t => t.Value);
                    foreach (List<string> t in additional)
                    {
                        read_files = read_files.Union(t).ToHashSet();
                    }
                }
                int after_count = read_files.Count;
                if (after_count == before_count)
                {
                    break;
                }
            }

            // Find rewrite rules, i.e., lexer rule "<TOKEN_REF> : <string literal>"
            Dictionary<string, string> subs = new Dictionary<string, string>();
            foreach (string f in read_files)
            {
                Workspaces.Document whatever_document = Workspaces.Workspace.Instance.FindDocument(f);
                if (whatever_document == null)
                {
                    continue;
                }
                AntlrGrammarDetails pd_whatever = ParserDetailsFactory.Create(whatever_document) as AntlrGrammarDetails;

                // Find literals in grammars.
                LiteralsGrammar lp_whatever = new LiteralsGrammar(pd_whatever);
                ParseTreeWalker.Default.Walk(lp_whatever, pd_whatever.ParseTree);
                List<TerminalNodeImpl> list_literals = lp_whatever.Literals;
                foreach (TerminalNodeImpl lexer_literal in list_literals)
                {
                    string old_name = lexer_literal.GetText();
                    // Given candidate, walk up tree to find lexer_rule.
                    /*
                        ( ruleSpec
                          ( lexerRuleSpec
                            ( OFF_CHANNEL text=\r\n\r\n
                            )
                            ( OFF_CHANNEL text=...
                            )
                            (OFF_CHANNEL text =\r\n\r\n
                            )
                            (OFF_CHANNEL text =...
                            )
                            (OFF_CHANNEL text =\r\n\r\n
                            )
                            (DEFAULT_TOKEN_CHANNEL i = 995 txt = NONASSOC tt = 1
                            )
                            (OFF_CHANNEL text =\r\n\t
                            )
                            (DEFAULT_TOKEN_CHANNEL i = 997 txt =: tt = 29
                            )
                            (lexerRuleBlock
                              (lexerAltList
                                (lexerAlt
                                  (lexerElements
                                    (lexerElement
                                      (lexerAtom
                                        (terminal
                                          (OFF_CHANNEL text =
                                          )
                                          (DEFAULT_TOKEN_CHANNEL i = 999 txt = '%binary' tt = 8
                            ))))))))
                            (OFF_CHANNEL text =\r\n\t
                            )
                            (DEFAULT_TOKEN_CHANNEL i = 1001 txt =; tt = 32
                        ) ) )

                     * Make sure it fits the structure of the tree shown above.
                     * 
                     */
                    IRuleNode p1 = lexer_literal.Parent;
                    if (p1.ChildCount != 1)
                    {
                        continue;
                    }

                    if (!(p1 is ANTLRv4Parser.TerminalContext))
                    {
                        continue;
                    }

                    IRuleNode p2 = p1.Parent;
                    if (p2.ChildCount != 1)
                    {
                        continue;
                    }

                    if (!(p2 is ANTLRv4Parser.LexerAtomContext))
                    {
                        continue;
                    }

                    IRuleNode p3 = p2.Parent;
                    if (p3.ChildCount != 1)
                    {
                        continue;
                    }

                    if (!(p3 is ANTLRv4Parser.LexerElementContext))
                    {
                        continue;
                    }

                    IRuleNode p4 = p3.Parent;
                    if (p4.ChildCount != 1)
                    {
                        continue;
                    }

                    if (!(p4 is ANTLRv4Parser.LexerElementsContext))
                    {
                        continue;
                    }

                    IRuleNode p5 = p4.Parent;
                    if (p5.ChildCount != 1)
                    {
                        continue;
                    }

                    if (!(p5 is ANTLRv4Parser.LexerAltContext))
                    {
                        continue;
                    }

                    IRuleNode p6 = p5.Parent;
                    if (p6.ChildCount != 1)
                    {
                        continue;
                    }

                    if (!(p6 is ANTLRv4Parser.LexerAltListContext))
                    {
                        continue;
                    }

                    IRuleNode p7 = p6.Parent;
                    if (p7.ChildCount != 1)
                    {
                        continue;
                    }

                    if (!(p7 is ANTLRv4Parser.LexerRuleBlockContext))
                    {
                        continue;
                    }

                    IRuleNode p8 = p7.Parent;
                    if (p8.ChildCount != 4)
                    {
                        continue;
                    }

                    if (!(p8 is ANTLRv4Parser.LexerRuleSpecContext))
                    {
                        continue;
                    }

                    IParseTree alt = p8.GetChild(0);
                    string new_name = alt.GetText();
                    subs.Add(old_name, new_name);
                }
            }

            // Determine where to put any new rules.
            string where_to_stuff = null;
            foreach (string f in read_files)
            {
                Workspaces.Document whatever_document = Workspaces.Workspace.Instance.FindDocument(f);
                if (whatever_document == null)
                {
                    continue;
                }
                AntlrGrammarDetails pd_whatever = ParserDetailsFactory.Create(whatever_document) as AntlrGrammarDetails;
                ExtractGrammarType x1 = new ExtractGrammarType();
                ParseTreeWalker.Default.Walk(x1, pd_whatever.ParseTree);
                bool is_right_grammar =
                           x1.Type == ExtractGrammarType.GrammarType.Combined
                           || x1.Type == ExtractGrammarType.GrammarType.Lexer;
                if (!is_right_grammar)
                    continue;
                if (where_to_stuff != null)
                    return null;
                where_to_stuff = f;
            }

            // Find string literals in parser and combined grammars and substitute.
            Dictionary<string, string> new_subs = new Dictionary<string, string>();
            foreach (string f in read_files)
            {
                Workspaces.Document whatever_document = Workspaces.Workspace.Instance.FindDocument(f);
                if (whatever_document == null)
                {
                    continue;
                }
                AntlrGrammarDetails pd_whatever = ParserDetailsFactory.Create(whatever_document) as AntlrGrammarDetails;
                StringBuilder sb = new StringBuilder();
                int pre = 0;
                Reconstruct(sb, pd_parser.TokStream, pd_parser.ParseTree, ref pre,
                n =>
                {
                    if (!(n is TerminalNodeImpl))
                    {
                        return null;
                    }
                    var t = n as TerminalNodeImpl;
                    if (t.Payload.Type != ANTLRv4Lexer.STRING_LITERAL)
                    {
                        return t.GetText();
                    }
                    bool no = false;
                        // Make sure this literal does not appear in lexer rule.
                        for (IRuleNode p = t.Parent; p != null; p = p.Parent)
                    {
                        if (p is ANTLRv4Parser.LexerRuleSpecContext)
                        {
                            no = true;
                            break;
                        }
                    }
                    if (no)
                    {
                        return t.GetText();
                    }
                    var r = t.GetText();
                    subs.TryGetValue(r, out string value);
                    if (value == null)
                    {
                        string now = DateTime.Now.ToString()
                            .Replace("/", "_")
                            .Replace(":", "_")
                            .Replace(" ", "_");
                        var new_r = "GENERATED_" + now;
                        subs[r] = new_r;
                        new_subs[r] = new_r;
                    }
                    return r;
                });
                var new_code = sb.ToString();
                if (new_code != pd_parser.Code)
                {
                    result.Add(f, new_code);
                }
            }
            if (new_subs.Count > 0)
            {
                result.TryGetValue(where_to_stuff, out string old_code);
                if (old_code == null)
                {
                    Workspaces.Document whatever_document = Workspaces.Workspace.Instance.FindDocument(where_to_stuff);
                    AntlrGrammarDetails pd_whatever = ParserDetailsFactory.Create(whatever_document) as AntlrGrammarDetails;
                    old_code = pd_whatever.Code;
                }
                StringBuilder sb = new StringBuilder();
                sb.Append(old_code);
                sb.AppendLine();
                foreach (var sub in new_subs)
                {
                    sb.AppendLine(sub.Value + " : " + sub.Key + " ;");
                }
                var new_code = sb.ToString();
                result[where_to_stuff] = new_code;
            }

            return result;
        }

        public static Dictionary<string, string> SortModes(Document document)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();

            // Check if lexer grammar.
            AntlrGrammarDetails pd_parser = ParserDetailsFactory.Create(document) as AntlrGrammarDetails;
            ExtractGrammarType lp = new ExtractGrammarType();
            ParseTreeWalker.Default.Walk(lp, pd_parser.ParseTree);
            bool is_lexer = lp.Type == ExtractGrammarType.GrammarType.Lexer;
            if (! is_lexer)
            {
                return result;
            }

            TableOfModes table = new TableOfModes(pd_parser, document);
            table.ReadModes();
            table.FindPartitions();

            // Find new order of modes.
            string old_code = document.Code;
            List<Pair<int, int>> reorder = new List<Pair<int, int>>();
            {
                List<string> ordered = table.modes
                    .Select(r => r.name)
                    .OrderBy(r => r).ToList();
                foreach (string s in ordered)
                {
                    TableOfModes.Row row = table.modes[table.name_to_index[s]];
                    reorder.Add(new Pair<int, int>(row.start_index, row.end_index));
                }
            }

            StringBuilder sb = new StringBuilder();
            int previous = 0;
            {
                int index_start = table.modes[0].start_index;
                int len = 0;
                string pre = old_code.Substring(previous, index_start - previous);
                sb.Append(pre);
                previous = index_start + len;
            }
            foreach (Pair<int, int> l in reorder)
            {
                int index_start = l.a;
                int len = l.b - l.a;
                string add = old_code.Substring(index_start, len);
                sb.Append(add);
            }
            //string rest = old_code.Substring(previous);
            //sb.Append(rest);
            string new_code = sb.ToString();
            result.Add(document.FullPath, new_code);

            return result;
        }
    }
}
