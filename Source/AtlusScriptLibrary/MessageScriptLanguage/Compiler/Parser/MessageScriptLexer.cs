//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//     ANTLR Version: 4.6.4
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------

// Generated from ..\..\..\AtlusScriptLibrary\MessageScriptLanguage\Compiler\Parser\MessageScriptLexer.g4 by ANTLR 4.6.4

// Unreachable code detected
#pragma warning disable 0162
// The variable '...' is assigned but its value is never used
#pragma warning disable 0219
// Missing XML comment for publicly visible type or member '...'
#pragma warning disable 1591
// Ambiguous reference in cref attribute
#pragma warning disable 419

namespace AtlusScriptLibrary.FlowScriptLanguage.Compiler.Parser {
using Antlr4.Runtime;
using Antlr4.Runtime.Atn;
using Antlr4.Runtime.Misc;
using DFA = Antlr4.Runtime.Dfa.DFA;

[System.CodeDom.Compiler.GeneratedCode("ANTLR", "4.6.4")]
[System.CLSCompliant(false)]
public partial class MessageScriptLexer : Lexer {
	public const int
		OpenCode=1, CloseText=2, LineComment=3, Text=4, MessageDialogTagId=5, 
		SelectionDialogTagId=6, SelectionDialogPatternId=7, CloseCode=8, OpenText=9, 
		IntLiteral=10, Identifier=11, Whitespace=12;
	public const int MessageScriptCode = 1;
	public static string[] modeNames = {
		"DEFAULT_MODE", "MessageScriptCode"
	};

	public static readonly string[] ruleNames = {
		"OpenCode", "CloseText", "LineComment", "Text", "MessageDialogTagId", 
		"SelectionDialogTagId", "SelectionDialogPatternId", "CloseCode", "OpenText", 
		"IdentifierEscape", "IntLiteral", "Identifier", "DecIntLiteral", "HexIntLiteral", 
		"Letter", "Digit", "HexDigit", "HexLiteralPrefix", "Sign", "Whitespace"
	};


	public MessageScriptLexer(ICharStream input)
		: base(input)
	{
		_interp = new LexerATNSimulator(this,_ATN);
	}

	private static readonly string[] _LiteralNames = {
		null, null, null, null, null, null, "'sel'"
	};
	private static readonly string[] _SymbolicNames = {
		null, "OpenCode", "CloseText", "LineComment", "Text", "MessageDialogTagId", 
		"SelectionDialogTagId", "SelectionDialogPatternId", "CloseCode", "OpenText", 
		"IntLiteral", "Identifier", "Whitespace"
	};
	public static readonly IVocabulary DefaultVocabulary = new Vocabulary(_LiteralNames, _SymbolicNames);

	[System.Obsolete("Use Vocabulary instead.")]
	public static readonly string[] tokenNames = GenerateTokenNames(DefaultVocabulary, _SymbolicNames.Length);

	private static string[] GenerateTokenNames(IVocabulary vocabulary, int length) {
		string[] tokenNames = new string[length];
		for (int i = 0; i < tokenNames.Length; i++) {
			tokenNames[i] = vocabulary.GetLiteralName(i);
			if (tokenNames[i] == null) {
				tokenNames[i] = vocabulary.GetSymbolicName(i);
			}

			if (tokenNames[i] == null) {
				tokenNames[i] = "<INVALID>";
			}
		}

		return tokenNames;
	}

	[System.Obsolete("Use IRecognizer.Vocabulary instead.")]
	public override string[] TokenNames
	{
		get
		{
			return tokenNames;
		}
	}

	[NotNull]
	public override IVocabulary Vocabulary
	{
		get
		{
			return DefaultVocabulary;
		}
	}

	public override string GrammarFileName { get { return "MessageScriptLexer.g4"; } }

	public override string[] RuleNames { get { return ruleNames; } }

	public override string[] ModeNames { get { return modeNames; } }

	public override string SerializedAtn { get { return _serializedATN; } }

	public static readonly string _serializedATN =
		"\x3\xAF6F\x8320\x479D\xB75C\x4880\x1605\x191C\xAB37\x2\xE\xAD\b\x1\b\x1"+
		"\x4\x2\t\x2\x4\x3\t\x3\x4\x4\t\x4\x4\x5\t\x5\x4\x6\t\x6\x4\a\t\a\x4\b"+
		"\t\b\x4\t\t\t\x4\n\t\n\x4\v\t\v\x4\f\t\f\x4\r\t\r\x4\xE\t\xE\x4\xF\t\xF"+
		"\x4\x10\t\x10\x4\x11\t\x11\x4\x12\t\x12\x4\x13\t\x13\x4\x14\t\x14\x4\x15"+
		"\t\x15\x3\x2\x3\x2\x3\x2\x3\x2\x3\x3\x3\x3\x3\x3\x3\x3\x3\x4\x3\x4\x3"+
		"\x4\x3\x4\a\x4\x39\n\x4\f\x4\xE\x4<\v\x4\x3\x4\x5\x4?\n\x4\x3\x4\x5\x4"+
		"\x42\n\x4\x3\x4\x3\x4\x3\x5\x6\x5G\n\x5\r\x5\xE\x5H\x3\x5\x3\x5\x5\x5"+
		"M\n\x5\x3\x6\x3\x6\x3\x6\x3\x6\x3\x6\x3\x6\x5\x6U\n\x6\x3\a\x3\a\x3\a"+
		"\x3\a\x3\b\x3\b\x3\b\x3\b\x3\b\x3\b\x3\b\x3\b\x3\b\x5\b\x64\n\b\x3\t\x3"+
		"\t\x3\t\x3\t\x3\n\x3\n\x3\n\x3\n\x3\v\x3\v\x3\v\x3\f\x3\f\x5\fs\n\f\x3"+
		"\r\x3\r\x5\rw\n\r\x3\r\x3\r\x3\r\a\r|\n\r\f\r\xE\r\x7F\v\r\x3\r\x3\r\a"+
		"\r\x83\n\r\f\r\xE\r\x86\v\r\x3\r\x3\r\x5\r\x8A\n\r\x3\xE\x5\xE\x8D\n\xE"+
		"\x3\xE\x6\xE\x90\n\xE\r\xE\xE\xE\x91\x3\xF\x5\xF\x95\n\xF\x3\xF\x3\xF"+
		"\x6\xF\x99\n\xF\r\xF\xE\xF\x9A\x3\x10\x3\x10\x3\x11\x3\x11\x3\x12\x3\x12"+
		"\x5\x12\xA3\n\x12\x3\x13\x3\x13\x3\x13\x3\x14\x3\x14\x3\x15\x3\x15\x3"+
		"\x15\x3\x15\x2\x2\x2\x16\x4\x2\x3\x6\x2\x4\b\x2\x5\n\x2\x6\f\x2\a\xE\x2"+
		"\b\x10\x2\t\x12\x2\n\x14\x2\v\x16\x2\x2\x18\x2\f\x1A\x2\r\x1C\x2\x2\x1E"+
		"\x2\x2 \x2\x2\"\x2\x2$\x2\x2&\x2\x2(\x2\x2*\x2\xE\x4\x2\x3\f\x4\x2\f\f"+
		"\xF\xF\x5\x2\x31\x31]]__\x3\x2\x31\x31\x3\x2\x62\x62\x4\x2\x43\\\x63|"+
		"\x3\x2\x32;\x4\x2\x43H\x63h\x4\x2ZZzz\x4\x2--//\x5\x2\v\f\xF\xF\"\"\xB6"+
		"\x2\x4\x3\x2\x2\x2\x2\x6\x3\x2\x2\x2\x2\b\x3\x2\x2\x2\x2\n\x3\x2\x2\x2"+
		"\x3\f\x3\x2\x2\x2\x3\xE\x3\x2\x2\x2\x3\x10\x3\x2\x2\x2\x3\x12\x3\x2\x2"+
		"\x2\x3\x14\x3\x2\x2\x2\x3\x18\x3\x2\x2\x2\x3\x1A\x3\x2\x2\x2\x3*\x3\x2"+
		"\x2\x2\x4,\x3\x2\x2\x2\x6\x30\x3\x2\x2\x2\b\x34\x3\x2\x2\x2\nL\x3\x2\x2"+
		"\x2\fT\x3\x2\x2\x2\xEV\x3\x2\x2\x2\x10\x63\x3\x2\x2\x2\x12\x65\x3\x2\x2"+
		"\x2\x14i\x3\x2\x2\x2\x16m\x3\x2\x2\x2\x18r\x3\x2\x2\x2\x1A\x89\x3\x2\x2"+
		"\x2\x1C\x8C\x3\x2\x2\x2\x1E\x94\x3\x2\x2\x2 \x9C\x3\x2\x2\x2\"\x9E\x3"+
		"\x2\x2\x2$\xA2\x3\x2\x2\x2&\xA4\x3\x2\x2\x2(\xA7\x3\x2\x2\x2*\xA9\x3\x2"+
		"\x2\x2,-\a]\x2\x2-.\x3\x2\x2\x2./\b\x2\x2\x2/\x5\x3\x2\x2\x2\x30\x31\a"+
		"_\x2\x2\x31\x32\x3\x2\x2\x2\x32\x33\b\x3\x2\x2\x33\a\x3\x2\x2\x2\x34\x35"+
		"\a\x31\x2\x2\x35\x36\a\x31\x2\x2\x36:\x3\x2\x2\x2\x37\x39\n\x2\x2\x2\x38"+
		"\x37\x3\x2\x2\x2\x39<\x3\x2\x2\x2:\x38\x3\x2\x2\x2:;\x3\x2\x2\x2;>\x3"+
		"\x2\x2\x2<:\x3\x2\x2\x2=?\a\xF\x2\x2>=\x3\x2\x2\x2>?\x3\x2\x2\x2?\x41"+
		"\x3\x2\x2\x2@\x42\a\f\x2\x2\x41@\x3\x2\x2\x2\x41\x42\x3\x2\x2\x2\x42\x43"+
		"\x3\x2\x2\x2\x43\x44\b\x4\x3\x2\x44\t\x3\x2\x2\x2\x45G\n\x3\x2\x2\x46"+
		"\x45\x3\x2\x2\x2GH\x3\x2\x2\x2H\x46\x3\x2\x2\x2HI\x3\x2\x2\x2IM\x3\x2"+
		"\x2\x2JK\a\x31\x2\x2KM\n\x4\x2\x2L\x46\x3\x2\x2\x2LJ\x3\x2\x2\x2M\v\x3"+
		"\x2\x2\x2NO\ao\x2\x2OP\au\x2\x2PU\ai\x2\x2QR\a\x66\x2\x2RS\an\x2\x2SU"+
		"\ai\x2\x2TN\x3\x2\x2\x2TQ\x3\x2\x2\x2U\r\x3\x2\x2\x2VW\au\x2\x2WX\ag\x2"+
		"\x2XY\an\x2\x2Y\xF\x3\x2\x2\x2Z[\av\x2\x2[\\\aq\x2\x2\\\x64\ar\x2\x2]"+
		"^\a\x64\x2\x2^_\aq\x2\x2_`\av\x2\x2`\x61\av\x2\x2\x61\x62\aq\x2\x2\x62"+
		"\x64\ao\x2\x2\x63Z\x3\x2\x2\x2\x63]\x3\x2\x2\x2\x64\x11\x3\x2\x2\x2\x65"+
		"\x66\a_\x2\x2\x66g\x3\x2\x2\x2gh\b\t\x4\x2h\x13\x3\x2\x2\x2ij\a]\x2\x2"+
		"jk\x3\x2\x2\x2kl\b\n\x4\x2l\x15\x3\x2\x2\x2mn\a\x62\x2\x2no\a\x62\x2\x2"+
		"o\x17\x3\x2\x2\x2ps\x5\x1C\xE\x2qs\x5\x1E\xF\x2rp\x3\x2\x2\x2rq\x3\x2"+
		"\x2\x2s\x19\x3\x2\x2\x2tw\x5 \x10\x2uw\a\x61\x2\x2vt\x3\x2\x2\x2vu\x3"+
		"\x2\x2\x2w}\x3\x2\x2\x2x|\x5 \x10\x2y|\a\x61\x2\x2z|\x5\"\x11\x2{x\x3"+
		"\x2\x2\x2{y\x3\x2\x2\x2{z\x3\x2\x2\x2|\x7F\x3\x2\x2\x2}{\x3\x2\x2\x2}"+
		"~\x3\x2\x2\x2~\x8A\x3\x2\x2\x2\x7F}\x3\x2\x2\x2\x80\x84\x5\x16\v\x2\x81"+
		"\x83\n\x5\x2\x2\x82\x81\x3\x2\x2\x2\x83\x86\x3\x2\x2\x2\x84\x82\x3\x2"+
		"\x2\x2\x84\x85\x3\x2\x2\x2\x85\x87\x3\x2\x2\x2\x86\x84\x3\x2\x2\x2\x87"+
		"\x88\x5\x16\v\x2\x88\x8A\x3\x2\x2\x2\x89v\x3\x2\x2\x2\x89\x80\x3\x2\x2"+
		"\x2\x8A\x1B\x3\x2\x2\x2\x8B\x8D\x5(\x14\x2\x8C\x8B\x3\x2\x2\x2\x8C\x8D"+
		"\x3\x2\x2\x2\x8D\x8F\x3\x2\x2\x2\x8E\x90\x5\"\x11\x2\x8F\x8E\x3\x2\x2"+
		"\x2\x90\x91\x3\x2\x2\x2\x91\x8F\x3\x2\x2\x2\x91\x92\x3\x2\x2\x2\x92\x1D"+
		"\x3\x2\x2\x2\x93\x95\x5(\x14\x2\x94\x93\x3\x2\x2\x2\x94\x95\x3\x2\x2\x2"+
		"\x95\x96\x3\x2\x2\x2\x96\x98\x5&\x13\x2\x97\x99\x5$\x12\x2\x98\x97\x3"+
		"\x2\x2\x2\x99\x9A\x3\x2\x2\x2\x9A\x98\x3\x2\x2\x2\x9A\x9B\x3\x2\x2\x2"+
		"\x9B\x1F\x3\x2\x2\x2\x9C\x9D\t\x6\x2\x2\x9D!\x3\x2\x2\x2\x9E\x9F\t\a\x2"+
		"\x2\x9F#\x3\x2\x2\x2\xA0\xA3\x5\"\x11\x2\xA1\xA3\t\b\x2\x2\xA2\xA0\x3"+
		"\x2\x2\x2\xA2\xA1\x3\x2\x2\x2\xA3%\x3\x2\x2\x2\xA4\xA5\a\x32\x2\x2\xA5"+
		"\xA6\t\t\x2\x2\xA6\'\x3\x2\x2\x2\xA7\xA8\t\n\x2\x2\xA8)\x3\x2\x2\x2\xA9"+
		"\xAA\t\v\x2\x2\xAA\xAB\x3\x2\x2\x2\xAB\xAC\b\x15\x3\x2\xAC+\x3\x2\x2\x2"+
		"\x16\x2\x3:>\x41HLT\x63rv{}\x84\x89\x8C\x91\x94\x9A\xA2\x5\a\x3\x2\b\x2"+
		"\x2\x6\x2\x2";
	public static readonly ATN _ATN =
		new ATNDeserializer().Deserialize(_serializedATN.ToCharArray());
}
} // namespace AtlusScriptLibrary.FlowScriptLanguage.Compiler.Parser
