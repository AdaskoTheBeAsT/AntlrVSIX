﻿namespace LanguageServer.Antlr
{
    using Antlr4.Runtime.Tree;
    using Symtab;
    using System.Collections.Generic;
    using System.Linq;

    public class AntlrParserDetails : ParserDetails
    {
        static Dictionary<string, IScope> _scopes = new Dictionary<string, IScope>();
        static Dictionary<string, Dictionary<IParseTree, Symtab.CombinedScopeSymbol>> _attributes = new Dictionary<string, Dictionary<IParseTree, Symtab.CombinedScopeSymbol>>();
        static IScope _global_scope = new SymbolTable().GLOBALS;

        public static Graphs.Utils.MultiMap<string, string> _dependent_grammars = new Graphs.Utils.MultiMap<string, string>();

        public AntlrParserDetails(Workspaces.Document item)
            : base(item)
        {
            // Passes executed in order for all files.
            Passes.Add(() =>
            {
                // Gather dependent grammars.
                ParseTreeWalker.Default.Walk(new Pass3Listener(this), ParseTree);
                return false;
            });
            Passes.Add(() =>
            {
                // A grammar gets its own symbol table if it's not included by any other file.
                // Otherwise, the grammar gets the symbol table of the grammar derived by
                // the transitive closure of the owner relationship.
                var dependent_grammars = _dependent_grammars;
                var file = this.Item.FullPath;
                for (; ; )
                {
                    dependent_grammars.TryGetValue(file, out List<string> dg);
                    if (dg == null) break;
                    file = dg.First();
                }

                // For all imported grammars, add in these to do if not in the workspace, and restart.
                foreach (KeyValuePair<string, List<string>> dep in dependent_grammars)
                {
                    var name = dep.Key;
                    var x = Workspaces.Workspace.Instance.FindDocument(name);
                    if (x == null)
                    {
                        // Add document.
                        var proj = this.Item.Parent;
                        var new_doc = new Workspaces.Document(name, name);
                        proj.AddChild(new_doc);
                        return true;
                    }
                    foreach (var y in dep.Value)
                    {
                        var z = Workspaces.Workspace.Instance.FindDocument(y);
                        if (z == null)
                        {
                            // Add document.
                            var proj = this.Item.Parent;
                            var new_doc = new Workspaces.Document(y, y);
                            proj.AddChild(new_doc);
                            return true;
                        }
                    }
                }

                _scopes.TryGetValue(file, out IScope value);
                if (value == null)
                {
                    value = new LocalScope(_global_scope);
                    _global_scope.nest(value);
                    _scopes[file] = value;
                }
                this.RootScope = value;
                _attributes.TryGetValue(file, out Dictionary<IParseTree, CombinedScopeSymbol> at);
                if (at == null)
                {
                    at = new Dictionary<IParseTree, CombinedScopeSymbol>();
                    _attributes[file] = at;
                }
                this.Attributes = at;


                return false;
            });
            Passes.Add(() =>
            {
                ParseTreeWalker.Default.Walk(new Pass1Listener(this), ParseTree);
                return false;
            });
            Passes.Add(() =>
            {
                ParseTreeWalker.Default.Walk(new Pass2Listener(this), ParseTree);
                return false;
            });
        }

        private void Nuke(IScope scope)
        {
            if (scope == null) return;
            foreach (var c in scope.NestedScopes)
            {
                Nuke(c);
            }
            var copy = scope.Symbols;
            foreach (var s in copy)
            {
                if (s.file == Item.FullPath)
                    scope.remove(s);
            }
        }

        public override void Cleanup()
        {
            var dir = Item.FullPath;
            dir = System.IO.Path.GetDirectoryName(dir);
            _scopes.TryGetValue(dir, out IScope value);
            if (value == null) return;
            Nuke(value);
            foreach (var s in value.NestedScopes)
            {
                // Nuke all symbols in this scope.
                Nuke(s);
            }
        }
    }
}
