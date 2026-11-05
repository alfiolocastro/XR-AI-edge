using System.Collections.Generic;
using System.IO;
using AIEdge.Chat;
using AIEdge.UI;
using NUnit.Framework;

namespace AIEdge.Tests
{
    public class MarkdownToTmpTests
    {
        [Test]
        public void Escape_BreaksRichTextTags()
        {
            var escaped = MarkdownToTmp.Escape("<b>hi</b>");
            StringAssert.DoesNotContain("<b>", escaped);
            StringAssert.Contains("hi", escaped);
        }

        [Test]
        public void Convert_BoldItalicCode()
        {
            Assert.AreEqual("<b>bold</b> and <i>it</i>", MarkdownToTmp.Convert("**bold** and *it*"));
            Assert.AreEqual("use <color=#9FD3FF>a*b*c</color>", MarkdownToTmp.Convert("use `a*b*c`"));
        }

        [Test]
        public void Convert_LeavesSnakeCaseAlone()
        {
            Assert.AreEqual("call water_plots now", MarkdownToTmp.Convert("call water_plots now"));
        }

        [Test]
        public void Convert_HeadingsAndLists()
        {
            Assert.AreEqual("<b>Title</b>\n• one\n  • two\n3. three", MarkdownToTmp.Convert("## Title\n- one\n  * two\n3) three"));
        }

        [Test]
        public void Convert_CodeFenceIsColoredAndNotStyled()
        {
            var result = MarkdownToTmp.Convert("a\n```\n**x**\n```\nb");
            Assert.AreEqual("a<color=#9FD3FF>\n**x**</color>\nb", result);
        }

        [Test]
        public void Convert_UnclosedFenceIsClosed()
        {
            StringAssert.EndsWith("</color>", MarkdownToTmp.Convert("```\ncode"));
        }

        [Test]
        public void Convert_EscapesModelTags()
        {
            StringAssert.DoesNotContain("<size", MarkdownToTmp.Convert("**<size=200>big**"));
        }
    }

    public class ChatContextTests
    {
        static List<ChatMessage> Messages(params string[] texts)
        {
            var list = new List<ChatMessage>();
            for (var i = 0; i < texts.Length; i++)
                list.Add(new ChatMessage { role = i % 2 == 0 ? ChatRole.User : ChatRole.Model, text = texts[i] });
            return list;
        }

        [Test]
        public void SelectHistory_KeepsAllWhenItFits()
        {
            var history = ChatContext.SelectHistory(Messages("hi", "hello", "how?", "fine"), 1000);
            Assert.AreEqual(4, history.Count);
            Assert.AreEqual(ChatRole.User, history[0].role);
        }

        [Test]
        public void SelectHistory_DropsOldestAndStartsAtUser()
        {
            // Budget fits the last three messages, but the oldest kept would be a model message.
            var history = ChatContext.SelectHistory(Messages("aaaa", "bbbb", "cccc", "dddd"), 12);
            Assert.AreEqual(2, history.Count);
            Assert.AreEqual("cccc", history[0].text);
            Assert.AreEqual(ChatRole.User, history[0].role);
        }

        [Test]
        public void SelectHistory_SkipsEmptyModelReplies()
        {
            var history = ChatContext.SelectHistory(Messages("q1", "", "q2", "a2"), 1000);
            Assert.AreEqual(3, history.Count);
        }

        [Test]
        public void ShouldTrim_AboveThreeQuarters()
        {
            Assert.IsFalse(ChatContext.ShouldTrim(3000, 4000));
            Assert.IsTrue(ChatContext.ShouldTrim(3001, 4000));
        }

        [Test]
        public void ToJson_WritesBareArray()
        {
            var json = ChatTurn.ToJson(new[] { new ChatTurn(ChatRole.User, "a\"b"), new ChatTurn(ChatRole.Model, "c") });
            Assert.AreEqual("[{\"role\":\"user\",\"text\":\"a\\\"b\"},{\"role\":\"model\",\"text\":\"c\"}]", json);
            Assert.AreEqual("", ChatTurn.ToJson(new ChatTurn[0]));
        }
    }

    public class ChatThreadStoreTests
    {
        string m_Root;

        [SetUp]
        public void SetUp()
        {
            m_Root = Path.Combine(Path.GetTempPath(), "AIEdgeChats_" + Path.GetRandomFileName());
            ChatThreadStore.SetDirectoryForTests(m_Root);
        }

        [TearDown]
        public void TearDown()
        {
            ChatThreadStore.SetDirectoryForTests(null);
            if (Directory.Exists(m_Root))
                Directory.Delete(m_Root, true);
        }

        [Test]
        public void SaveLoad_RoundTrips()
        {
            var thread = ChatThread.Create("Be brief.");
            thread.Add(ChatRole.User, "What is LiteRT?");
            var reply = thread.Add(ChatRole.Model, "A runtime.");
            reply.stats = new LlmStats { ttftSec = 0.5, turnTokens = 3 };
            ChatThreadStore.Save(thread);

            var loaded = ChatThreadStore.Load(thread.id);
            Assert.AreEqual("What is LiteRT?", loaded.title);
            Assert.AreEqual("Be brief.", loaded.systemPrompt);
            Assert.AreEqual(2, loaded.messages.Count);
            Assert.AreEqual(ChatRole.Model, loaded.messages[1].role);
            Assert.AreEqual(3, loaded.messages[1].stats.turnTokens);
        }

        [Test]
        public void LoadAll_NewestFirst_SkipsCorrupt()
        {
            var older = ChatThread.Create();
            older.updatedUnixMs = 1;
            var newer = ChatThread.Create();
            newer.updatedUnixMs = 2;
            ChatThreadStore.Save(older);
            ChatThreadStore.Save(newer);
            File.WriteAllText(Path.Combine(m_Root, "broken.json"), "{not json");

            var all = ChatThreadStore.LoadAll();
            Assert.AreEqual(2, all.Count);
            Assert.AreEqual(newer.id, all[0].id);
        }

        [Test]
        public void Delete_RemovesThread()
        {
            var thread = ChatThread.Create();
            ChatThreadStore.Save(thread);
            ChatThreadStore.Delete(thread.id);
            Assert.IsNull(ChatThreadStore.Load(thread.id));
        }

        [Test]
        public void MakeTitle_TrimsAndShortens()
        {
            Assert.AreEqual("New chat", ChatThread.MakeTitle("  \n "));
            Assert.AreEqual("a b", ChatThread.MakeTitle(" a \n  b "));
            Assert.AreEqual(40, ChatThread.MakeTitle(new string('x', 80)).Length);
        }
    }
}
