﻿' Copyright (C) 2023  Andy https://github.com/AAndyProgram
' This program is free software: you can redistribute it and/or modify
' it under the terms of the GNU General Public License as published by
' the Free Software Foundation, either version 3 of the License, or
' (at your option) any later version.
'
' This program is distributed in the hope that it will be useful,
' but WITHOUT ANY WARRANTY
Imports System.Net
Imports System.Threading
Imports PersonalUtilities.Functions.XML
Imports PersonalUtilities.Functions.Messaging
Imports PersonalUtilities.Functions.RegularExpressions
Imports PersonalUtilities.Tools.Web.Clients
Imports PersonalUtilities.Tools.Web.Documents.JSON
Imports SCrawler.API.Base
Imports UTypes = SCrawler.API.Base.UserMedia.Types
Namespace API.Instagram
    Friend Class UserData : Inherits UserDataBase
#Region "XML Names"
        Private Const Name_LastCursor As String = "LastCursor"
        Private Const Name_FirstLoadingDone As String = "FirstLoadingDone"
        Private Const Name_GetStories As String = "GetStories"
        Private Const Name_GetTagged As String = "GetTaggedData"
        Private Const Name_TaggedChecked As String = "TaggedChecked"
#End Region
#Region "Declarations"
        Private ReadOnly Property MySiteSettings As SiteSettings
            Get
                Return DirectCast(HOST.Source, SiteSettings)
            End Get
        End Property
        Private ReadOnly _SavedPostsIDs As New List(Of String)
        Private LastCursor As String = String.Empty
        Private FirstLoadingDone As Boolean = False
        Friend Property GetStories As Boolean
        Friend Property GetTaggedData As Boolean
#End Region
#Region "Exchange options"
        Friend Overrides Function ExchangeOptionsGet() As Object
            Return New EditorExchangeOptions(HOST.Source) With {.GetStories = GetStories, .GetTagged = GetTaggedData}
        End Function
        Friend Overrides Sub ExchangeOptionsSet(ByVal Obj As Object)
            If Not Obj Is Nothing AndAlso TypeOf Obj Is EditorExchangeOptions Then
                With DirectCast(Obj, EditorExchangeOptions)
                    GetStories = .GetStories
                    GetTaggedData = .GetTagged
                End With
            End If
        End Sub
#End Region
#Region "Initializer, loader"
        Friend Sub New()
        End Sub
        Protected Overrides Sub LoadUserInformation_OptionalFields(ByRef Container As XmlFile, ByVal Loading As Boolean)
            If Loading Then
                LastCursor = Container.Value(Name_LastCursor)
                FirstLoadingDone = Container.Value(Name_FirstLoadingDone).FromXML(Of Boolean)(False)
                GetStories = Container.Value(Name_GetStories).FromXML(Of Boolean)(CBool(MySiteSettings.GetStories.Value))
                GetTaggedData = Container.Value(Name_GetTagged).FromXML(Of Boolean)(CBool(MySiteSettings.GetTagged.Value))
                TaggedChecked = Container.Value(Name_TaggedChecked).FromXML(Of Boolean)(False)
            Else
                Container.Add(Name_LastCursor, LastCursor)
                Container.Add(Name_FirstLoadingDone, FirstLoadingDone.BoolToInteger)
                Container.Add(Name_GetStories, GetStories.BoolToInteger)
                Container.Add(Name_GetTagged, GetTaggedData.BoolToInteger)
                Container.Add(Name_TaggedChecked, TaggedChecked.BoolToInteger)
            End If
        End Sub
#End Region
#Region "Download data"
        Private E560Thrown As Boolean = False
        Private Class ExitException : Inherits Exception
            Friend Shared Sub Throw560(ByRef Source As UserData)
                If Not Source.E560Thrown Then
                    MyMainLOG = $"{Source.ToStringForLog}: (560) Download skipped until next session"
                    Source.E560Thrown = True
                End If
                Throw New ExitException
            End Sub
            Friend Sub New()
            End Sub
            Friend Sub New(ByRef CompleteArg As Boolean)
                CompleteArg = True
            End Sub
        End Class
        Protected Overrides Sub DownloadDataF(ByVal Token As CancellationToken)
            Dim s As Sections = Sections.Timeline
            Try
                ThrowAny(Token)
                _InstaHash = String.Empty
                HasError = False
                Dim fc As Boolean = IIf(IsSavedPosts, MySiteSettings.DownloadSaved.Value, MySiteSettings.DownloadTimeline.Value)
                If fc And Not LastCursor.IsEmptyString Then
                    s = IIf(IsSavedPosts, Sections.SavedPosts, Sections.Timeline)
                    DownloadData(LastCursor, s, Token)
                    ThrowAny(Token)
                    If Not HasError Then FirstLoadingDone = True
                End If
                If fc And Not HasError Then
                    s = IIf(IsSavedPosts, Sections.SavedPosts, Sections.Timeline)
                    DownloadData(String.Empty, s, Token)
                    ThrowAny(Token)
                    If Not HasError Then FirstLoadingDone = True
                End If
                If FirstLoadingDone Then LastCursor = String.Empty
                If IsSavedPosts Then
                    If MySiteSettings.DownloadSaved Then s = Sections.SavedPosts : DownloadPosts(Token)
                ElseIf MySiteSettings.BaseAuthExists() Then
                    DownloadedTags = 0
                    If MySiteSettings.DownloadStoriesTagged And GetStories Then s = Sections.Stories : DownloadData(String.Empty, s, Token)
                    If MySiteSettings.DownloadStoriesTagged And GetTaggedData Then s = Sections.Tagged : DownloadData(String.Empty, s, Token)
                End If
                If WaitNotificationMode = WNM.SkipTemp Or WaitNotificationMode = WNM.SkipCurrent Then WaitNotificationMode = WNM.Notify
            Catch eex As ExitException
            Catch ex As Exception
                ProcessException(ex, Token, "[API.Instagram.UserData.DownloadDataF]", False, s)
            Finally
                E560Thrown = False
            End Try
        End Sub
        Private _InstaHash As String = String.Empty
        Private Enum Sections : Timeline : Tagged : Stories : SavedPosts : End Enum
        Private Const StoriesFolder As String = "Stories"
        Private Const TaggedFolder As String = "Tagged"
#Region "429 bypass"
        Private Const MaxPostsCount As Integer = 200
        Friend Property RequestsCount As Integer = 0
        Friend Enum WNM As Integer
            Notify = 0
            SkipCurrent = 1
            SkipAll = 2
            SkipTemp = 3
        End Enum
        Friend WaitNotificationMode As WNM = WNM.Notify
        Private Caught429 As Boolean = False
        Private ProgressTempSet As Boolean = False
        Private Const InstAborted As String = "InstAborted"
        Private Function Ready() As Boolean
            With MySiteSettings
                If Not .ReadyForDownload Then
                    If WaitNotificationMode = WNM.Notify Then
                        Dim m As New MMessage("Instagram [too many requests] error." & vbCr &
                                              $"The program suggests waiting {If(.LastApplyingValue, 0)} minutes." & vbCr &
                                              "What do you want to do?", "Waiting for Instagram download...",
                                              {
                                               New MsgBoxButton("Wait") With {.ToolTip = "Wait and ask again when the error is found."},
                                               New MsgBoxButton("Wait (disable current") With {.ToolTip = "Wait and skip future prompts while downloading the current profile."},
                                               New MsgBoxButton("Abort") With {.ToolTip = "Abort operation"},
                                               New MsgBoxButton("Wait (disable all)") With {.ToolTip = "Wait and skip future prompts while downloading the current session."}
                                              },
                                              vbExclamation) With {.ButtonsPerRow = 2, .DefaultButton = 0, .CancelButton = 2}
                        Select Case MsgBoxE(m).Index
                            Case 1 : WaitNotificationMode = WNM.SkipCurrent
                            Case 2 : Throw New OperationCanceledException("Instagram download operation aborted") With {.HelpLink = InstAborted}
                            Case 3 : WaitNotificationMode = WNM.SkipAll
                            Case Else : WaitNotificationMode = WNM.SkipTemp
                        End Select
                    End If
                    If Not ProgressTempSet Then Progress.InformationTemporary = $"Waiting until { .GetWaitDate().ToString(ParsersDataDateProvider)}"
                    ProgressTempSet = True
                    Return False
                Else
                    Return True
                End If
            End With
        End Function
        Private Sub ReconfigureAwaiter()
            If WaitNotificationMode = WNM.SkipTemp Then WaitNotificationMode = WNM.Notify
            If Caught429 Then Caught429 = False
            ProgressTempSet = False
        End Sub
        Private Sub NextRequest(ByVal StartWait As Boolean)
            With MySiteSettings
                If StartWait And RequestsCount > 0 And (RequestsCount Mod .RequestsWaitTimerTaskCount.Value) = 0 Then Thread.Sleep(CInt(.RequestsWaitTimer.Value))
                If RequestsCount >= MaxPostsCount - 5 Then Thread.Sleep(CInt(.SleepTimerOnPostsLimit.Value))
            End With
        End Sub
#End Region
#Region "Tags"
        Private TaggedChecked As Boolean = False
        Friend TaggedCheckSession As Boolean = True
        Private DownloadedTags As Integer = 0
        Private DownloadTagsLimit As Integer? = Nothing
        Private ReadOnly Property TaggedLimitsNotifications(Optional ByVal v As Integer? = Nothing) As Boolean
            Get
                Return Not TaggedChecked AndAlso TaggedCheckSession AndAlso
                       CInt(MySiteSettings.TaggedNotifyLimit.Value) > 0 AndAlso
                       (Not v.HasValue OrElse v.Value > CInt(MySiteSettings.TaggedNotifyLimit.Value))
            End Get
        End Property
        Private Function SetTagsLimit(ByVal Max As Integer, ByVal p As ANumbers) As DialogResult
            Dim v%?
            Dim aStr$ = $"Enter the number of posts from user {ToString()} that you want to download{vbCr}" &
                        $"(Max: {Max.NumToString(p)}; Requests: {(Max / 12).RoundUp.NumToString(p)})"
            Dim tryBtt As New MsgBoxButton("Try again") With {.ToolTip = "You will be asked again about the limit"}
            Dim cancelBtt As New MsgBoxButton("Cancel") With {.ToolTip = "Cancel tagged posts download operation"}
            Dim selectBtt As New MsgBoxButton("Other options") With {.ToolTip = "The main message with options will be displayed again"}
            Dim m As New MMessage("You have not entered a valid posts limit", "Tagged posts download limit", {tryBtt, selectBtt, cancelBtt})
            Dim mh As New MMessage("", "Tagged posts download limit", {"Confirm", tryBtt, selectBtt, cancelBtt}) With {.ButtonsPerRow = 2}
            Do
                v = AConvert(Of Integer)(InputBoxE(aStr, "Tagged posts download limit", CInt(MySiteSettings.TaggedNotifyLimit.Value)), AModes.Var, Nothing)
                If v.HasValue Then
                    mh.Text = $"You have entered a limit of {v.Value.NumToString(p)} posts"
                    Select Case MsgBoxE(mh).Index
                        Case 0 : DownloadTagsLimit = v : Return DialogResult.OK
                        Case 1 : v = Nothing
                        Case 2 : Return DialogResult.Retry
                        Case 3 : Return DialogResult.Cancel
                    End Select
                Else
                    Select Case MsgBoxE(m).Index
                        Case 1 : Return DialogResult.Retry
                        Case 2 : Return DialogResult.Cancel
                    End Select
                End If
            Loop While Not v.HasValue
            Return DialogResult.Retry
        End Function
        Private Function TaggedContinue(ByVal TaggedCount As Integer) As DialogResult
            Dim agi As New ANumbers With {.FormatOptions = ANumbers.Options.GroupIntegral}
            Dim msg As New MMessage($"The number of tagged posts by user [{ToString()}] is {TaggedCount.NumToString(agi)}" & vbCr &
                                    $"This is about {(TaggedCount / 12).RoundUp.NumToString(agi)} requests." & vbCr &
                                    "The tagged data download operation can take a long time.",
                                    "Too much tagged data",
                                    {
                                        "Continue",
                                        New MsgBoxButton("Continue unnotified") With {
                                            .ToolTip = "Continue downloading and cancel further notifications in the current downloading session."},
                                        New MsgBoxButton("Limit") With {
                                            .ToolTip = "Enter the limit of posts you want to download."},
                                        New MsgBoxButton("Disable and cancel") With {
                                            .ToolTip = "Disable downloading tagged data and cancel downloading tagged data."},
                                        "Cancel"
                                    }, MsgBoxStyle.Exclamation) With {.DefaultButton = 0, .CancelButton = 4, .ButtonsPerRow = 2}
            Do
                Select Case MsgBoxE(msg).Index
                    Case 0 : Return DialogResult.OK
                    Case 1 : TaggedCheckSession = False : Return DialogResult.OK
                    Case 2
                        Select Case SetTagsLimit(TaggedCount, agi)
                            Case DialogResult.OK : Return DialogResult.OK
                            Case DialogResult.Cancel : Return DialogResult.Cancel
                        End Select
                    Case 3 : GetTaggedData = False : Return DialogResult.Cancel
                    Case 4 : Return DialogResult.Cancel
                End Select
            Loop
        End Function
#End Region
        Private Overloads Sub DownloadData(ByVal Cursor As String, ByVal Section As Sections, ByVal Token As CancellationToken)
            Dim URL$ = String.Empty
            Dim StoriesList As List(Of String) = Nothing
            Dim StoriesRequested As Boolean = False
            Dim _DownloadComplete As Boolean = False
            LastCursor = Cursor
            Try
                Do While Not _DownloadComplete
                    ThrowAny(Token)
                    If Not Ready() Then Thread.Sleep(10000) : ThrowAny(Token) : Continue Do
                    ReconfigureAwaiter()

                    Try
                        Dim n As EContainer, nn As EContainer, node As EContainer
                        Dim HasNextPage As Boolean = False
                        Dim Pinned As Boolean
                        Dim EndCursor$ = String.Empty
                        Dim PostID$ = String.Empty, PostDate$ = String.Empty, SpecFolder$ = String.Empty
                        Dim TaggedCount%
                        Dim ENode() As Object = Nothing
                        NextRequest(True)

                        'Check environment
                        If Cursor.IsEmptyString And _InstaHash.IsEmptyString Then _
                           _InstaHash = CStr(If(IsSavedPosts, MySiteSettings.HashSavedPosts, MySiteSettings.Hash).Value)
                        If ID.IsEmptyString Then GetUserId()
                        If ID.IsEmptyString Then Throw New ArgumentException("User ID is not detected", "ID")

                        'Create query
                        Select Case Section
                            Case Sections.Timeline, Sections.SavedPosts
                                Dim vars$ = "{""id"":" & ID & ",""first"":50,""after"":""" & Cursor & """}"
                                vars = SymbolsConverter.ASCII.EncodeSymbolsOnly(vars)
                                URL = $"https://www.instagram.com/graphql/query/?query_hash={_InstaHash}&variables={vars}"
                                ENode = {"data", "user", 0}
                            Case Sections.Tagged
                                URL = $"https://i.instagram.com/api/v1/usertags/{ID}/feed/?count=50&max_id={Cursor}"
                                ENode = {"items"}
                                SpecFolder = TaggedFolder
                            Case Sections.Stories
                                If Not StoriesRequested Then
                                    StoriesList = GetStoriesList()
                                    StoriesRequested = True
                                    MySiteSettings.TooManyRequests(False)
                                    RequestsCount += 1
                                    ThrowAny(Token)
                                End If
                                If StoriesList.ListExists Then
                                    GetStoriesData(StoriesList, Token)
                                    MySiteSettings.TooManyRequests(False)
                                    RequestsCount += 1
                                End If
                                If StoriesList.ListExists Then
                                    Continue Do
                                Else
                                    Throw New ExitException(_DownloadComplete)
                                End If
                        End Select

                        'Get response
                        Dim r$ = Responser.GetResponse(URL,, EDP.ThrowException)
                        MySiteSettings.TooManyRequests(False)
                        RequestsCount += 1
                        ThrowAny(Token)

                        'Parsing
                        If Not r.IsEmptyString Then
                            Using j As EContainer = JsonDocument.Parse(r).XmlIfNothing
                                n = j.ItemF(ENode).XmlIfNothing
                                If n.Count > 0 Then
                                    Select Case Section
                                        Case Sections.Timeline, Sections.SavedPosts
                                            If n.Contains("page_info") Then
                                                With n("page_info")
                                                    HasNextPage = .Value("has_next_page").FromXML(Of Boolean)(False)
                                                    EndCursor = .Value("end_cursor")
                                                End With
                                            End If
                                            n = n("edges").XmlIfNothing
                                            If n.Count > 0 Then
                                                For Each nn In n
                                                    ThrowAny(Token)
                                                    node = nn(0).XmlIfNothing
                                                    If IsSavedPosts Then
                                                        PostID = node.Value("shortcode")
                                                        If Not PostID.IsEmptyString AndAlso _TempPostsList.Contains(PostID) Then Throw New ExitException(_DownloadComplete)
                                                    End If
                                                    PostID = node.Value("id")
                                                    Pinned = CBool(If(node("pinned_for_users")?.Count, 0))
                                                    If Not PostID.IsEmptyString And _TempPostsList.Contains(PostID) And Not Pinned Then Throw New ExitException(_DownloadComplete)
                                                    _TempPostsList.Add(PostID)
                                                    PostDate = node.Value("taken_at_timestamp")
                                                    If IsSavedPosts Then
                                                        _SavedPostsIDs.Add(PostID)
                                                    Else
                                                        Select Case CheckDatesLimit(PostDate, DateProvider)
                                                            Case DateResult.Skip : Continue For
                                                            Case DateResult.Exit : If Not Pinned Then Throw New ExitException(_DownloadComplete)
                                                        End Select
                                                        ObtainMedia(node, PostID, PostDate, SpecFolder)
                                                    End If
                                                Next
                                            End If
                                        Case Sections.Tagged
                                            HasNextPage = j.Value("more_available").FromXML(Of Boolean)(False)
                                            EndCursor = j.Value("next_max_id")
                                            For Each nn In n
                                                PostID = $"Tagged_{nn.Value("id")}"
                                                If Not PostID.IsEmptyString And _TempPostsList.Contains(PostID) Then Throw New ExitException(_DownloadComplete)
                                                _TempPostsList.Add(PostID)
                                                ObtainMedia2(nn, PostID, SpecFolder)
                                                DownloadedTags += 1
                                                If DownloadTagsLimit.HasValue AndAlso DownloadedTags >= DownloadTagsLimit.Value Then Throw New ExitException(_DownloadComplete)
                                            Next
                                            If TaggedLimitsNotifications Then
                                                TaggedCount = j.Value("total_count").FromXML(Of Integer)(0)
                                                TaggedChecked = True
                                                If TaggedLimitsNotifications(TaggedCount) AndAlso
                                                   TaggedContinue(TaggedCount) = DialogResult.Cancel Then Throw New ExitException(_DownloadComplete)
                                            End If
                                    End Select
                                Else
                                    If j.Value("status") = "ok" AndAlso j({"data", "user"}).XmlIfNothing.Count = 0 AndAlso
                                       _TempMediaList.Count = 0 AndAlso Section = Sections.Timeline Then _
                                       UserExists = False : Throw New ExitException(_DownloadComplete)
                                End If
                            End Using
                        Else
                            Throw New ExitException(_DownloadComplete)
                        End If
                        _DownloadComplete = True
                        If HasNextPage And Not EndCursor.IsEmptyString Then DownloadData(EndCursor, Section, Token)
                    Catch eex As ExitException
                        Throw eex
                    Catch oex As OperationCanceledException When Token.IsCancellationRequested
                        Exit Do
                    Catch dex As ObjectDisposedException When Disposed
                        Exit Do
                    Catch ex As Exception
                        If DownloadingException(ex, $"data downloading error [{URL}]", False, Section) = 1 Then Continue Do Else Exit Do
                    End Try
                Loop
            Catch eex2 As ExitException
                If (Section = Sections.Timeline Or Section = Sections.Tagged) And Not Cursor.IsEmptyString Then Throw eex2
            Catch oex2 As OperationCanceledException When Token.IsCancellationRequested Or oex2.HelpLink = InstAborted
                If oex2.HelpLink = InstAborted Then HasError = True
            Catch DoEx As Exception
                ProcessException(DoEx, Token, $"data downloading error [{URL}]",, Section)
            End Try
        End Sub
        Private Sub DownloadPosts(ByVal Token As CancellationToken)
            Dim URL$ = String.Empty
            Dim _DownloadComplete As Boolean = False
            Dim _Index% = 0
            Try
                Do While Not _DownloadComplete
                    ThrowAny(Token)
                    If Not Ready() Then Thread.Sleep(10000) : ThrowAny(Token) : Continue Do
                    ReconfigureAwaiter()

                    Try
                        Dim r$
                        Dim j As EContainer, jj As EContainer
                        Dim _MediaObtained As Boolean
                        If _SavedPostsIDs.Count > 0 And _Index <= _SavedPostsIDs.Count - 1 Then
                            Dim e As New ErrorsDescriber(EDP.ThrowException)
                            For i% = _Index To _SavedPostsIDs.Count - 1
                                _Index = i
                                'URL = $"https://instagram.com/p/{_SavedPostsIDs(i)}/?__a=1"
                                URL = $"https://i.instagram.com/api/v1/media/{_SavedPostsIDs(i)}/info/"
                                ThrowAny(Token)
                                NextRequest(((i + 1) Mod 5) = 0)
                                ThrowAny(Token)
                                r = Responser.GetResponse(URL,, e)
                                MySiteSettings.TooManyRequests(False)
                                RequestsCount += 1
                                If Not r.IsEmptyString Then
                                    j = JsonDocument.Parse(r)
                                    If Not j Is Nothing Then
                                        _MediaObtained = False
                                        If j.Contains({"graphql", "shortcode_media"}) Then
                                            With j({"graphql", "shortcode_media"}).XmlIfNothing
                                                If .Count > 0 Then ObtainMedia(.Self, _SavedPostsIDs(i), String.Empty, String.Empty) : _MediaObtained = True
                                            End With
                                        End If
                                        If Not _MediaObtained AndAlso j.Contains("items") Then
                                            With j("items")
                                                If .Count > 0 Then
                                                    For Each jj In .Self : ObtainMedia2(jj, _SavedPostsIDs(i)) : Next
                                                End If
                                            End With
                                        End If
                                        j.Dispose()
                                    End If
                                End If
                            Next
                        End If
                        _DownloadComplete = True
                    Catch eex As ExitException
                        Throw eex
                    Catch oex As OperationCanceledException When Token.IsCancellationRequested
                        Exit Do
                    Catch dex As ObjectDisposedException When Disposed
                        Exit Do
                    Catch ex As Exception
                        If DownloadingException(ex, $"downloading saved posts error [{URL}]", False, Sections.SavedPosts) = 1 Then Continue Do Else Exit Do
                    End Try
                Loop
            Catch eex2 As ExitException
            Catch oex2 As OperationCanceledException When Token.IsCancellationRequested Or oex2.HelpLink = InstAborted
                If oex2.HelpLink = InstAborted Then HasError = True
            Catch DoEx As Exception
                ProcessException(DoEx, Token, $"downloading saved posts error [{URL}]",, Sections.SavedPosts)
            End Try
        End Sub
#End Region
#Region "Code ID converters"
        Private Shared Function CodeToID(ByVal Code As String) As String
            Const CodeSymbols$ = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"
            Try
                If Not Code.IsEmptyString Then
                    Dim c As Char
                    Dim id& = 0
                    For i% = 0 To Code.Length - 1
                        c = Code(i)
                        id = (id * 64) + CodeSymbols.IndexOf(c)
                    Next
                    Return id
                Else
                    Return String.Empty
                End If
            Catch ex As Exception
                Return ErrorsDescriber.Execute(EDP.SendInLog, ex, $"[API.Instagram.UserData.CodeToID({Code})", String.Empty)
            End Try
        End Function
#End Region
#Region "Obtain Media"
        Private Sub ObtainMedia(ByVal node As EContainer, ByVal PostID As String, ByVal PostDate As String, ByVal SpecFolder As String)
            Dim CreateMedia As Action(Of EContainer) =
                Sub(ByVal e As EContainer)
                    Dim t As UTypes = If(e.Value("is_video").FromXML(Of Boolean)(False), UTypes.Video, UTypes.Picture)
                    Dim tmpValue$
                    If t = UTypes.Picture Then
                        tmpValue = e.Value("display_url")
                    Else
                        tmpValue = e.Value("video_url")
                    End If
                    If Not tmpValue.IsEmptyString Then _TempMediaList.ListAddValue(MediaFromData(t, tmpValue, PostID, PostDate, SpecFolder), LNC)
                End Sub
            If node.Contains({"edge_sidecar_to_children", "edges"}) Then
                For Each edge As EContainer In node({"edge_sidecar_to_children", "edges"}) : CreateMedia(edge("node").XmlIfNothing) : Next
            Else
                CreateMedia(node)
            End If
        End Sub
        Private Sub ObtainMedia2(ByVal n As EContainer, ByVal PostID As String, Optional ByVal SpecialFolder As String = Nothing,
                                 Optional ByVal DateObj As String = Nothing)
            Try
                Dim img As Predicate(Of EContainer) = Function(_img) Not _img.Name.IsEmptyString AndAlso _img.Name.StartsWith("image_versions") AndAlso _img.Count > 0
                Dim vid As Predicate(Of EContainer) = Function(_vid) Not _vid.Name.IsEmptyString AndAlso _vid.Name.StartsWith("video_versions") AndAlso _vid.Count > 0
                Dim ss As Func(Of EContainer, Sizes) = Function(_ss) New Sizes(_ss.Value("width"), _ss.Value("url"))
                Dim mDate As Func(Of EContainer, String) = Function(ByVal elem As EContainer) As String
                                                               If Not DateObj.IsEmptyString Then Return DateObj
                                                               If elem.Contains("taken_at") Then
                                                                   Return elem.Value("taken_at")
                                                               ElseIf elem.Contains("imported_taken_at") Then
                                                                   Return elem.Value("imported_taken_at")
                                                               Else
                                                                   Dim ev$ = elem.Value("device_timestamp")
                                                                   If Not ev.IsEmptyString Then
                                                                       If ev.Length > 10 Then
                                                                           Return ev.Substring(0, 10)
                                                                       Else
                                                                           Return ev
                                                                       End If
                                                                   Else
                                                                       Return String.Empty
                                                                   End If
                                                               End If
                                                           End Function
                If n.Count > 0 Then
                    Dim l As New List(Of Sizes)
                    Dim d As EContainer
                    Dim t%
                    '8 - gallery
                    '2 - one video
                    '1 - one picture
                    t = n.Value("media_type").FromXML(Of Integer)(-1)
                    If t >= 0 Then
                        Select Case t
                            Case 1
                                If n.Contains(img) Then
                                    t = n.Value("media_type").FromXML(Of Integer)(-1)
                                    DateObj = mDate(n)
                                    If t >= 0 Then
                                        With n.ItemF({img, "candidates"}).XmlIfNothing
                                            If .Count > 0 Then
                                                l.Clear()
                                                l.ListAddList(.Select(ss), LNC)
                                                If l.Count > 0 Then
                                                    l.Sort()
                                                    _TempMediaList.ListAddValue(MediaFromData(UTypes.Picture, l.First.Data, PostID, DateObj, SpecialFolder), LNC)
                                                    l.Clear()
                                                End If
                                            End If
                                        End With
                                    End If
                                End If
                            Case 2
                                If n.Contains(vid) Then
                                    DateObj = mDate(n)
                                    With n.ItemF({vid}).XmlIfNothing
                                        If .Count > 0 Then
                                            l.Clear()
                                            l.ListAddList(.Select(ss), LNC)
                                            If l.Count > 0 Then
                                                l.Sort()
                                                _TempMediaList.ListAddValue(MediaFromData(UTypes.Video, l.First.Data, PostID, DateObj, SpecialFolder), LNC)
                                                l.Clear()
                                            End If
                                        End If
                                    End With
                                End If
                            Case 8
                                DateObj = mDate(n)
                                With n("carousel_media").XmlIfNothing
                                    If .Count > 0 Then
                                        For Each d In .Self : ObtainMedia2(d, PostID, SpecialFolder, DateObj) : Next
                                    End If
                                End With
                        End Select
                    End If
                    l.Clear()
                End If
            Catch ex As Exception
                ErrorsDescriber.Execute(EDP.SendInLog, ex, "API.Instagram.ObtainMedia2")
            End Try
        End Sub
#End Region
#Region "GetUserId"
        <Obsolete> Private Sub GetUserId_Old()
            Try
                Dim r$ = Responser.GetResponse($"https://www.instagram.com/{Name}/?__a=1",, EDP.ThrowException)
                If Not r.IsEmptyString Then
                    Using j As EContainer = JsonDocument.Parse(r).XmlIfNothing
                        ID = j({"graphql", "user"}, "id").XmlIfNothingValue
                    End Using
                End If
            Catch ex As Exception
                If Responser.StatusCode = HttpStatusCode.NotFound Or Responser.StatusCode = HttpStatusCode.BadRequest Then
                    Throw ex
                Else
                    LogError(ex, "get Instagram user id")
                End If
            End Try
        End Sub
        Private Sub GetUserId()
            Try
                Dim r$ = Responser.GetResponse($"https://i.instagram.com/api/v1/users/web_profile_info/?username={Name}",, EDP.ThrowException)
                If Not r.IsEmptyString Then
                    Using j As EContainer = JsonDocument.Parse(r).XmlIfNothing
                        ID = j({"data", "user"}, "id").XmlIfNothingValue
                    End Using
                End If
            Catch ex As Exception
                If Responser.StatusCode = HttpStatusCode.NotFound Or Responser.StatusCode = HttpStatusCode.BadRequest Then
                    Throw ex
                Else
                    LogError(ex, "get Instagram user id")
                End If
            End Try
        End Sub
#End Region
#Region "Pinned stories"
        Private Sub GetStoriesData(ByRef StoriesList As List(Of String), ByVal Token As CancellationToken)
            Const ReqUrl$ = "https://i.instagram.com/api/v1/feed/reels_media/?{0}"
            Dim tmpList As IEnumerable(Of String)
            Dim qStr$, r$, sFolder$, storyID$, pid$
            Dim i% = -1
            Dim jj As EContainer, s As EContainer
            ThrowAny(Token)
            If StoriesList.ListExists Then
                tmpList = StoriesList.Take(5)
                If tmpList.ListExists Then
                    qStr = String.Format(ReqUrl, tmpList.Select(Function(q) $"reel_ids=highlight:{q}").ListToString("&"))
                    r = Responser.GetResponse(qStr,, EDP.ThrowException)
                    ThrowAny(Token)
                    If Not r.IsEmptyString Then
                        Using j As EContainer = JsonDocument.Parse(r).XmlIfNothing
                            If j.Contains("reels") Then
                                For Each jj In j("reels")
                                    i += 1
                                    sFolder = jj.Value("title").StringRemoveWinForbiddenSymbols
                                    storyID = jj.Value("id").Replace("highlight:", String.Empty)
                                    If sFolder.IsEmptyString Then sFolder = $"Story_{storyID}"
                                    If sFolder.IsEmptyString Then sFolder = $"Story_{i}"
                                    sFolder = $"{StoriesFolder}\{sFolder}"
                                    If Not storyID.IsEmptyString Then storyID &= ":"
                                    With jj("items").XmlIfNothing
                                        If .Count > 0 Then
                                            For Each s In .Self
                                                pid = storyID & s.Value("id")
                                                If Not _TempPostsList.Contains(pid) Then
                                                    ThrowAny(Token)
                                                    ObtainMedia2(s, pid, sFolder)
                                                    _TempPostsList.Add(pid)
                                                End If
                                            Next
                                        End If
                                    End With
                                Next
                            End If
                        End Using
                    End If
                    StoriesList.RemoveRange(0, tmpList.Count)
                End If
            End If
        End Sub
        Private Function GetStoriesList() As List(Of String)
            Try
                Dim r$ = Responser.GetResponse($"https://i.instagram.com/api/v1/highlights/{ID}/highlights_tray/",, EDP.ThrowException)
                If Not r.IsEmptyString Then
                    Using j As EContainer = JsonDocument.Parse(r).XmlIfNothing()("tray").XmlIfNothing
                        If j.Count > 0 Then Return j.Select(Function(jj) jj.Value("id").Replace("highlight:", String.Empty)).ListIfNothing
                    End Using
                End If
                Return Nothing
            Catch ex As Exception
                DownloadingException(ex, "API.Instagram.GetStoriesList", False, Sections.Stories)
                Return Nothing
            End Try
        End Function
#End Region
#Region "Download content"
        Protected Overrides Sub DownloadContent(ByVal Token As CancellationToken)
            DownloadContentDefault(Token)
        End Sub
#End Region
#Region "Exceptions"
        ''' <exception cref="ExitException"></exception>
        ''' <inheritdoc cref="UserDataBase.ThrowAny(CancellationToken)"/>
        Friend Overrides Sub ThrowAny(ByVal Token As CancellationToken)
            If MySiteSettings.SkipUntilNextSession Then ExitException.Throw560(Me)
            MyBase.ThrowAny(Token)
        End Sub
        ''' <summary>
        ''' <inheritdoc cref="UserDataBase.DownloadingException(Exception, String, Boolean, Object)"/><br/>
        ''' 1 - continue
        ''' </summary>
        Protected Overrides Function DownloadingException(ByVal ex As Exception, ByVal Message As String, Optional ByVal FromPE As Boolean = False,
                                                          Optional ByVal s As Object = Nothing) As Integer
            If Responser.StatusCode = HttpStatusCode.NotFound Then
                UserExists = False
            ElseIf Responser.StatusCode = HttpStatusCode.BadRequest Then
                HasError = True
                MyMainLOG = $"Instagram credentials have expired [{CInt(Responser.StatusCode)}]: {ToStringForLog()} [{s}]"
                DisableSection(s)
            ElseIf Responser.StatusCode = HttpStatusCode.Forbidden And s = Sections.Tagged Then
                Return 3
            ElseIf Responser.StatusCode = 429 Then
                With MySiteSettings
                    Dim WaiterExists As Boolean = .LastApplyingValue.HasValue
                    .TooManyRequests(True)
                    If Not WaiterExists Then .LastApplyingValue = 2
                End With
                Caught429 = True
                MyMainLOG = $"Number of requests before error 429: {RequestsCount}"
                Return 1
            ElseIf Responser.StatusCode = 560 Then
                MySiteSettings.SkipUntilNextSession = True
            Else
                MyMainLOG = $"Instagram hash requested [{CInt(Responser.StatusCode)}]: {ToString()} [{s}]"
                DisableSection(s)
                If Not FromPE Then LogError(ex, Message) : HasError = True
                Return 0
            End If
            Return 2
        End Function
        Private Sub DisableSection(ByVal Section As Object)
            If Not IsNothing(Section) AndAlso TypeOf Section Is Sections Then
                Dim s As Sections = DirectCast(Section, Sections)
                Select Case s
                    Case Sections.Timeline : MySiteSettings.DownloadTimeline.Value = False
                    Case Sections.SavedPosts : MySiteSettings.DownloadSaved.Value = False
                    Case Else : MySiteSettings.DownloadStoriesTagged.Value = False
                End Select
                MyMainLOG = $"[{s}] downloading is disabled until you update your credentials".ToUpper
            End If
        End Sub
#End Region
#Region "Create media"
        Private Shared Function MediaFromData(ByVal t As UTypes, ByVal _URL As String, ByVal PostID As String, ByVal PostDate As String,
                                              Optional ByVal SpecialFolder As String = Nothing) As UserMedia
            _URL = LinkFormatterSecure(RegexReplace(_URL.Replace("\", String.Empty), LinkPattern))
            Dim m As New UserMedia(_URL, t) With {.Post = New UserPost With {.ID = PostID}}
            If Not m.URL.IsEmptyString Then m.File = CStr(RegexReplace(m.URL, FilesPattern))
            If Not PostDate.IsEmptyString Then m.Post.Date = AConvert(Of Date)(PostDate, DateProvider, Nothing) Else m.Post.Date = Nothing
            m.SpecialFolder = SpecialFolder
            Return m
        End Function
#End Region
#Region "Standalone downloader"
        Friend Shared Function GetVideoInfo(ByVal URL As String, ByVal r As Response) As IEnumerable(Of UserMedia)
            Try
                If Not URL.IsEmptyString AndAlso URL.Contains("instagram.com") Then
                    Dim PID$ = RegexReplace(URL, RParams.DMS(".*?instagram.com/p/([_\w\d]+)", 1))
                    If Not PID.IsEmptyString AndAlso Not ACheck(Of Long)(PID) Then PID = CodeToID(PID)
                    If Not PID.IsEmptyString Then
                        Using t As New UserData
                            t.SetEnvironment(Settings(InstagramSiteKey), Nothing, False, False)
                            t.Responser = New Response
                            t.Responser.Copy(r)
                            t._SavedPostsIDs.Add(PID)
                            t.DownloadPosts(Nothing)
                            Return ListAddList(Nothing, t._TempMediaList)
                        End Using
                    End If
                End If
                Return Nothing
            Catch ex As Exception
                Return ErrorsDescriber.Execute(EDP.ShowMainMsg + EDP.SendInLog, ex, $"Instagram standalone downloader: fetch media error ({URL})")
            End Try
        End Function
#End Region
#Region "IDisposable Support"
        Protected Overrides Sub Dispose(ByVal disposing As Boolean)
            If Not disposedValue And disposing Then _SavedPostsIDs.Clear()
            MyBase.Dispose(disposing)
        End Sub
#End Region
    End Class
End Namespace