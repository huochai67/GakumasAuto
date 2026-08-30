using System.Collections.Generic;

namespace GakumasAuto
{
    internal class LayoutNodeDto
    {
        public int id { get; set; }
        public string path { get; set; }
        public string name { get; set; }
        public int sx { get; set; }
        public int sy { get; set; }
        public float w { get; set; }
        public float h { get; set; }
        public bool active { get; set; }
        public List<string> flags { get; set; }
        public string text { get; set; }
    }

    internal class LayoutRespDto
    {
        public string root { get; set; }
        public int count { get; set; }
        public List<LayoutNodeDto> nodes { get; set; }
    }

    internal class Layout2Dto
    {
        public int w { get; set; }
        public int h { get; set; }
        public int count { get; set; }
        public bool truncated { get; set; }
        public string tree { get; set; }
    }

    internal class StateDto
    {
        public string userId { get; set; }
        public string name { get; set; }
        public string topLayer { get; set; }
        public bool layersActive { get; set; }
        public bool loading { get; set; }
        public string maintenance { get; set; }
        public string screen { get; set; }
    }

    internal class ScreenshotDto
    {
        public string file { get; set; }
        public int w { get; set; }
        public int h { get; set; }
        public bool pending { get; set; }
    }

    internal class FindMatchDto
    {
        public string path { get; set; }
        public string name { get; set; }
        public bool active { get; set; }
        public string text { get; set; }
    }

    internal class FindRespDto
    {
        public int count { get; set; }
        public List<FindMatchDto> matches { get; set; }
    }

    internal class WorkStatusDto
    {
        public string type { get; set; }
        public string name { get; set; }
        public string state { get; set; }
        public string characterId { get; set; }
        public string characterName { get; set; }
        public int level { get; set; }
        public int durationMinutes { get; set; }
        public long startedTime { get; set; }
        public long remainingSeconds { get; set; }
        public int skipCount { get; set; }
        public int totalFinishCount { get; set; }
        public bool fixedIsExcellent { get; set; }
    }

    internal class DailyStateDto
    {
        public List<WorkStatusDto> works { get; set; }
        public int moneyUnreceived { get; set; }
        public int moneyUnreceivedElapsedSeconds { get; set; }
        public long moneyLastReceivedTime { get; set; }
        public bool moneyReceivable { get; set; }
        public int jewelFree { get; set; }
        public int jewelPaid { get; set; }
        public int jewelTotal { get; set; }
        public string error { get; set; }
    }

    internal class ShopItemDto
    {
        public string id { get; set; }
        public string shopId { get; set; }
        public string name { get; set; }
        public string assetId { get; set; }
        public int price { get; set; }
        public int totalJewelQuantity { get; set; }
        public int paidOnlyJewelQuantity { get; set; }
        public int purchaseLimit { get; set; }
        public int purchasedCount { get; set; }
        public int purchasedOrder { get; set; }
        public bool unlocked { get; set; }
        public bool isSoldOut { get; set; }
        public bool noti { get; set; }
        public bool isFree { get; set; }
        public long endTime { get; set; }
        public long nextResetTime { get; set; }
        public int order { get; set; }
        public string consumptionResource { get; set; }
        public int consumptionQuantity { get; set; }
        public List<string> labelTypes { get; set; }
    }

    internal class ShopListDto
    {
        public bool shopScreenOpen { get; set; }
        public List<string> shops { get; set; }
        public List<ShopItemDto> items { get; set; }
        public string error { get; set; }
    }

    internal class ExchangeShopDto
    {
        public string id { get; set; }
        public string name { get; set; }
        public string type { get; set; }
        public bool isNew { get; set; }
        public bool isLocked { get; set; }
        public bool isMaintenance { get; set; }
    }

    internal class ExchangeListDto
    {
        public bool selectScreenOpen { get; set; }
        public bool listScreenOpen { get; set; }
        public string currentId { get; set; }
        public List<ExchangeShopDto> exchanges { get; set; }
        public string error { get; set; }
    }

    internal class MissionDto
    {
        public string id { get; set; }
        public string name { get; set; }
        public string category { get; set; }
        public string type { get; set; }
        public string groupId { get; set; }
        public string groupName { get; set; }
        public long progress { get; set; }
        public int threshold { get; set; }
        public string state { get; set; }
        public bool receivable { get; set; }
        public int receivableCount { get; set; }
        public bool allClear { get; set; }
        public bool received { get; set; }
        public bool unlocked { get; set; }
        public bool isEvent { get; set; }
        public int order { get; set; }
    }

    internal class MissionCategorySummaryDto
    {
        public string category { get; set; }
        public int total { get; set; }
        public int receivable { get; set; }
        public int inProgress { get; set; }
        public int locked { get; set; }
        public int cleared { get; set; }
        public int maxPoint { get; set; }
    }

    internal class MissionListDto
    {
        public string filter { get; set; }
        public List<MissionCategorySummaryDto> summary { get; set; }
        public List<MissionDto> missions { get; set; }
        public string error { get; set; }
    }

    internal class ExamCardDto
    {
        public string id { get; set; }
        public string guid { get; set; }
        public string name { get; set; }
        public string rarity { get; set; }
        public int upgradeCount { get; set; }
        public int customizeCount { get; set; }
        public int evaluation { get; set; }
        public int staminaCost { get; set; }
        public string category { get; set; }
        public string planType { get; set; }
    }

    internal class ExamStateDto
    {
        public bool active { get; set; }
        public bool isInProgress { get; set; }
        public bool isCommandPlaying { get; set; }
        public bool isPauseTurnEnd { get; set; }
        public bool isShowTurnEndButton { get; set; }
        public bool isBusy { get; set; }
        public bool isInitialized { get; set; }
        public int selectCardIndex { get; set; }
        public int recommendIndex { get; set; }
        public int currentPlayerSequenceIndex { get; set; }
        public string statusType { get; set; }
        public ExamCardDto playingCard { get; set; }
        public List<ExamCardDto> hand { get; set; }
        public List<ExamCardDto> deck { get; set; }
        public List<ExamCardDto> grave { get; set; }
        public List<ExamCardDto> lost { get; set; }
        public List<ExamCardDto> hold { get; set; }
        public List<List<ExamCardDto>> futureDeck { get; set; }
        public string error { get; set; }
    }

    internal class AccountStateDto
    {
        public string userId { get; set; }
        public string name { get; set; }
        public int producerLevel { get; set; }
        public long exp { get; set; }
        public int expToNext { get; set; }
        public float expProgress { get; set; }
        public bool levelMax { get; set; }
        public long totalFan { get; set; }
        public long money { get; set; }
        public int jewelFree { get; set; }
        public int jewelPaid { get; set; }
        public int jewelTotal { get; set; }
        public int actionPoint { get; set; }
        public int actionPointMax { get; set; }
        public int challengePoint { get; set; }
        public int competitionRemaining { get; set; }
        public string error { get; set; }
    }

    internal class ItemDto
    {
        public string id { get; set; }
        public string name { get; set; }
        public string type { get; set; }
        public long quantity { get; set; }
        public long expiryTime { get; set; }
        public string rarity { get; set; }
    }

    internal class ItemListDto
    {
        public int count { get; set; }
        public long money { get; set; }
        public List<ItemDto> items { get; set; }
        public string error { get; set; }
    }

    internal class GiftDto
    {
        public string id { get; set; }
        public string name { get; set; }
        public string resourceId { get; set; }
        public string resourceType { get; set; }
        public long quantity { get; set; }
        public string message { get; set; }
        public long createdAt { get; set; }
        public long limitTime { get; set; }
        public bool unreceivable { get; set; }
    }

    internal class GiftListDto
    {
        public bool screenOpen { get; set; }
        public int total { get; set; }
        public List<GiftDto> gifts { get; set; }
        public string error { get; set; }
    }

    internal class PvpStateDto
    {
        public string grade { get; set; }
        public string bestGrade { get; set; }
        public string phaseType { get; set; }
        public int rank { get; set; }
        public int rate { get; set; }
        public int remainingDailyPlayCount { get; set; }
        public int maxDailyPlayCount { get; set; }
        public string seasonStatus { get; set; }
        public long seasonEndTime { get; set; }
        public long rivalMatchTime { get; set; }
        public int competitionRemaining { get; set; }
        public int challengePoint { get; set; }
        public bool screenOpen { get; set; }
        public List<PvpRivalDto> rivals { get; set; }
        public string error { get; set; }
    }

    internal class PvpRivalDto
    {
        public string type { get; set; }
        public string name { get; set; }
        public int totalPower { get; set; }
        public int earnedRate { get; set; }
        public bool isNpc { get; set; }
    }

    internal class ProduceStateDto
    {
        public bool inProgress { get; set; }
        public bool inProgressStep { get; set; }
        public string produceId { get; set; }
        public string produceName { get; set; }
        public string produceType { get; set; }
        public string produceGroupId { get; set; }
        public string produceGroupName { get; set; }
        public string characterId { get; set; }
        public string characterName { get; set; }
        public string idolCardId { get; set; }
        public string idolCardName { get; set; }
        public int producePoint { get; set; }
        public int stamina { get; set; }
        public int maxStamina { get; set; }
        public int produceScore { get; set; }
        public string status { get; set; }
        public string stepType { get; set; }
        public string stepKind { get; set; }
        public string stepId { get; set; }
        public int stepNumber { get; set; }
        public int week { get; set; }
        public int day { get; set; }
        public List<string> currentOptions { get; set; }
        public string error { get; set; }
    }

    internal class ProduceScheduleDto
    {
        public int stepNumber { get; set; }
        public string selectedStepType { get; set; }
        public List<string> stepTypes { get; set; }
        public int auditionRank { get; set; }
    }

    internal class ProduceShopItemDto
    {
        public int position { get; set; }
        public string name { get; set; }
        public string resourceId { get; set; }
        public string resourceType { get; set; }
        public int price { get; set; }
        public int nextPrice { get; set; }
        public bool purchased { get; set; }
        public bool locked { get; set; }
        public int upgradeCount { get; set; }
        public bool discount { get; set; }
    }

    internal class ProduceShopListDto
    {
        public bool inProgress { get; set; }
        public bool shopScreenOpen { get; set; }
        public int producePoint { get; set; }
        public List<ProduceShopItemDto> items { get; set; }
        public string error { get; set; }
    }

    internal class ProduceOutingOptionDto
    {
        public string type { get; set; }
        public string name { get; set; }
        public int stamina { get; set; }
        public int producePoint { get; set; }
        public int fanVoteValue { get; set; }
        public int number { get; set; }
    }

    internal class ProduceOutingDto
    {
        public bool outingScreenOpen { get; set; }
        public List<ProduceOutingOptionDto> options { get; set; }
        public string error { get; set; }
    }

    internal class ProduceCardDto
    {
        public int number { get; set; }
        public string produceCardId { get; set; }
        public string name { get; set; }
        public int upgradeCount { get; set; }
        public bool customizing { get; set; }
        public bool deleted { get; set; }
        public bool canCustomize { get; set; }
    }

    internal class ProduceCardListDto
    {
        public bool customizeScreenOpen { get; set; }
        public int remainingCustomizeCount { get; set; }
        public List<ProduceCardDto> cards { get; set; }
        public string error { get; set; }
    }

    internal class ProduceScheduleListDto
    {
        public bool inProgress { get; set; }
        public int currentStepNumber { get; set; }
        public List<ProduceScheduleDto> days { get; set; }
        public string error { get; set; }
    }

    internal class ClubStateDto
    {
        public bool screenOpen { get; set; }
        public bool inGuild { get; set; }
        public string guildId { get; set; }
        public string guildName { get; set; }
        public string requestState { get; set; }
        public bool canRequest { get; set; }
        public bool canReceive { get; set; }
        public string requestedItemId { get; set; }
        public string requestedItemName { get; set; }
        public int remainDonationCount { get; set; }
        public int maxDonationCount { get; set; }
        public string donationState { get; set; }
        public int memberCount { get; set; }
        public List<string> members { get; set; }
        public string error { get; set; }
    }

    internal class CapsuleDto
    {
        public string id { get; set; }
        public string name { get; set; }
        public string type { get; set; }
        public string kind { get; set; }
        public bool locked { get; set; }
        public bool noti { get; set; }
        public string consumption { get; set; }
        public int consumptionQuantity { get; set; }
        public int maxDraw { get; set; }
        public int totalDrawCount { get; set; }
    }

    internal class CapsuleListDto
    {
        public bool screenOpen { get; set; }
        public List<CapsuleDto> gashas { get; set; }
        public string error { get; set; }
    }

    internal class SupportCardDto
    {
        public string id { get; set; }
        public string name { get; set; }
        public int level { get; set; }
        public int levelLimit { get; set; }
        public int stock { get; set; }
        public string planType { get; set; }
        public int rarity { get; set; }
    }

    internal class SupportCardListDto
    {
        public int count { get; set; }
        public List<SupportCardDto> cards { get; set; }
        public string error { get; set; }
    }

    internal class ExchangeProductDto
    {
        public string id { get; set; }
        public string name { get; set; }
        public bool recommend { get; set; }
        public bool unlocked { get; set; }
        public int exchangeLimit { get; set; }
        public int exchangedCount { get; set; }
        public int price { get; set; }
        public string consumptionResource { get; set; }
        public string rewardType { get; set; }
        public string rewardId { get; set; }
        public long rewardQuantity { get; set; }
        public int order { get; set; }
    }

    internal class ExchangeProductListDto
    {
        public bool listScreenOpen { get; set; }
        public string currentId { get; set; }
        public string currentName { get; set; }
        public bool manualResettable { get; set; }
        public int resetCount { get; set; }
        public List<ExchangeProductDto> items { get; set; }
        public string error { get; set; }
    }
}
