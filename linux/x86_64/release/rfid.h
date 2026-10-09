
/**
 * @file rfid.h
 *
 * @brief rfid reader sdk header file
 */

/**@mainpage rfid读写器sdk c库/rfid reader sdk c library
 *
 * # 简介/brief introduction
 *
 * 本库提供rfid读写器所有的功能包括：<p>
 *
 * This library provides all functions of RFID reader including: <p>
 *
 * - rfid标签读、写、盘点、锁定、毁灭等功能
 * - rfid读写器管理功能
 * - 一些工具函数
 *<p>
 * - RFID tag reading, writing, inventory, locking, destruction and other functions
 * - RFID reader management function
 * - Some utility functions
 *
 * # 基本用法/Basic Usage
 *
 * ## 设备扫描功能/Device scanning
 *
 * 可以扫描到局域网内的已联网的设备<p>
 * Can scan to the network of devices in the LAN<p>
 *
 * - 定义回调/Define the callback
 *
 * @code
 static void test_device_scan_callback(rfid_device device) {
  printf("device: %s %d %s\n", device.ip, device.port, device.id);
}
 * @endcode
 *
 * - 开始扫描/Start Scan
 *
 * @code
 int ret = start_device_scan(test_device_scan_callback);
 * @endcode
 *
 * - 结束扫描/Stop Scan
 *
 * @code
 ret = stop_device_scan();
 * @endcode
 *
 * ## 连接设备/Connecting
 *
 * 本库支持tcp，udp，rs232，rs485方式连接设备（实际看设备支持哪种）<p>
 * This library supports TCP, UDP, RS232, RS485 to connect the device (actually depends on which device supports)<p>
 *
 * - tcp
 *
 * @code
 * int ret = connect_net("192.168.1.223", 1969, tcp, &id);
 * @endcode
 *
 * - udp
 *
 * @code
 * int ret = connect_net("192.168.1.223", 1969, udp, &id);
 * @endcode
 *
 * - rs232
 *
 * @code
 * int ret = connect_rs((char *) "COM3", baud115200, 0, &id);
 * @endcode
 *
 * - rs485
 *
 * @code
 * int ret = connect_rs((char *) "COM3", baud115200, 33, &id);
 * @endcode
 *
 * 连接成功之后，返回本次连接的id，该id用作标识本连接<p>
 * After successful connection, return the ID of this connection, which is used to identify this connection
 *
 * ## 标签操作/Tag operation
 *
 * ### 列出附近的标签/List nearby tags
 *
 *  本功能只能在命令模式下使用<p>
 *  This feature can only be used in command mode<p>
 *
 * @code
 unsigned char password[4] = {0, 0, 0, 0};
  int count;
  tag **tags;
  int ret =
      list6c(id, memory_bank_epc, 4, 4, password, 4, &tags, &count);
  // assert(ret == err_ok);
  if (ret != err_ok) {
    return;
  }
  for (int i = 0; i < count; ++i) {
    char epc_hex_str[256];
    to_hex_str(tags[i]->id, tags[i]->len, epc_hex_str);
    printf("%d: list 6c epc: [%s]\n", i, epc_hex_str);
  }
 * @endcode
 *
 * > 注意使用完之后释放内存 `c_free(tags);` <p>
 * > Free memory after use `c_free(tags);` <p>
 *
 * ### 读标签/Reading
 *
 * 以下代码展示读取user区0-12字节的数据<p>
 * The following code shows reading 0-12 bytes of data from the user memory<p>
 *
 * @code
  unsigned char user[256];
  ret = read6c(id, memory_bank_user, 0, 12, epc, epc_len,
                 password, 4, user, &dataLen);
 * @endcode
 *
 * ### 写标签/Writing
 *
 *  以下代码展示写入user区0开始12个字节的数据<p>
 *  The following code shows writing 0-12 bytes of data from the user memory<p>
 *
 * @code
  ret = write6c(id, memory_bank_user, 0, epc,epc_len, user, 12,
                  password, 4);
 * @endcode
 *
 * ### 监听自动模式下输出/Listen the output in automatic mode
 *
 * 当读写器处于自动工作模式时，以下代码可以监听自动输出的数据<p>
 * When the reader is in auto mode, the following code can listen for auto-output data
 *
 * > 用户可以自定义输出的数据字段，可以通过`set_output_format`或者读写器管理程序来修改,
 * > 参考后面的章节[自定义上报格式](@ref lable_define_output_format) <p>
 * > Users can customize the output data fields. This can be modified using 'set_output_format' or the reader
 * > hypervisor. Refer to the following section [Custom Output Format](@ref lable_define_output_format)
 *
 * - 定义回调/Define the callback
 *
 * @code
 void testAutoReadCB(auto_read_output item) {
  printf("testAutoReadCB auto read: %s\n", item.epc);
}
 * @endcode
 *
 * - 开始监听/Start Scan
 *
* @code
 ret = start_listen_auto_read(id, testAutoReadCB);
 * @endcode
 *
 * - 结束监听/Stop Scan
 *
 * @code
 * ret = stop_listen_auto_read(id);
 * @endcode
 *
 *
 * ### 自定义上报格式/Custom report format ###                         {#lable_define_output_format}
 *
 * - 设置格式字段/format fields setting
 *
 * @code
 output_format format;
  memset(&format, 0, sizeof(output_format));
  format.format = format.format | format_flag_ant | format_flag_epc |
                  format_flag_ip | format_flag_tid;
 * @endcode
 *
 * - 测试某个字段是否设置了/check format field
 *
 *@code
 assert(format.format & format_flag_ant);
  assert(format.format & format_flag_epc);
  assert(format.format & format_flag_ip);
  assert(format.format & format_flag_tid);
 * @endcode
 *
 *
 * - 设置格式/output format setting
 *
 * @code
  int ret = set_output_format(id, format);
  assert(ret == err_ok);
 * @endcode
 *
 *
 */

#ifndef _RFID_H_
#define _RFID_H_

#if !defined(WIN32)
#define __declspec(dllexport)
#endif

#include <stddef.h>
#include <stdint.h>

#define err_ok 0

#define MAX_EPC_LEN 256
#define MAX_USER_LEN 256
#define MAX_TID_LEN 256

#define MAX_HF_UID_LEN 10

#define MAX_IP_LEN 15
#define MAX_WIFI_PASSWORD_LEN 64
#define MAX_WIFI_SSID_LEN 32
#define MAX_ID_LEN 24
#define MAX_CUSTOM_LEN 24
#define MAX_TRIGGER_COUNT 16
typedef struct _VariableSizedStructure
{
  int length;
  char buffer[1];
} VariableSizedStructure;

/**
 * @brief 传输协议/transport protocol
 */
typedef enum transport_protocol
{
  tcp,
  udp
} transport_protocol;

/**
 * @brief 波特率/Baud rate
 */
typedef enum baud
{
  baud9600,
  baud19200,
  baud38400,
  baud57600,
  baud115200
} baud;

/**
 * @brief 硬件接口/hardware interface
 */
typedef enum port
{
  /// RS232接口/RS232 interface
  rs232,
  /// RS485接口/RS485 interface
  rs485,
  /// 以太网接口/Ethernet network interface
  rj45,
  /// 韦根26/Wiegand-26
  wg26,
  /// 韦根34/Wiegand-34
  wg34,
  /// WiFi接口/WiFi interface
  wifi,
  /// GSM接口(4g/5g)/GSM interface (4g/5g)
  gsm4g,
} port;

/**
 * @brief 继电器状态/relay status
 */
typedef enum relay_status
{
  /// 继电器断开/Relay off
  relay_closed,
  /// 继电器闭合/relay closure
  relay_open
} relay_status;

/**
 * @brief 电平/electrical level
 */
typedef enum electrical_level
{
  /// 高电平/high level
  high,
  /// 低电平/low level
  low
} electrical_level;

/**
 * @brief 自动模式下输出数据/Output data in automatic mode
 *
 *
 */
typedef struct auto_read_output
{
  /// 天线号/Antenna NO
  int ant;
  /**
   * 表示开关触发模式下哪一路被触发了，它的值可能是[1,2,3]。该字段只在开关触发模式下才会上报。<p>
   * Indicates which triggers are triggered in trigger mode. Its value may be [1,2,3]. This field is reported only in
   * triggered mode.
   */
  int fin[MAX_TRIGGER_COUNT];
  /// 被触发的触发器数量/Number of triggers
  int fin_count;
  /**
   * 表示通道门模式下的触发方向，从触发1到触发2为`In`，从触发2到触发1为`Out`。该字段只在通道门模式下才会上报。<p>
   * Indicates the trigger direction In the gate mode. from trigger 1 to trigger 2 is 'IN', and from trigger 2 to
   * trigger 1 is 'OUT'. This field is only reported in Gateway mode
   */
  char door[4];
  /// 读写器的IP地址/The IP address of the reader
  char ip[MAX_IP_LEN + 1];
  /// 高频卡UID
  char uid[16 + 1];
  /// EPC
  char epc[MAX_EPC_LEN * 2 + 1];
  /// TID
  char tid[MAX_TID_LEN * 2 + 1];
  /// USER
  char user[MAX_USER_LEN * 2 + 1];
  /// 读写器的设备ID//The device ID of the reader
  char id[MAX_ID_LEN + 1];
  /// 标签的信号强度/rssi
  int rssi;
  /// 识别到该标签的时间戳(unix time)/timestamp
  int ts;
  /// 标签的类型。1--6C,2--6B/The type of the tag. 1--6C,2--6B
  int tagType;
  /// 用户自定义字段1/User defined field 1
  char custom1[MAX_CUSTOM_LEN + 1];
  /// 用户自定义字段2/User defined field 2
  char custom2[MAX_CUSTOM_LEN + 1];
  /// 用户自定义字段3//User defined field 3
  char custom3[MAX_CUSTOM_LEN + 1];
  /// 用户自定义字段4//User defined field 4
  char custom4[MAX_CUSTOM_LEN + 1];
  /// 用户自定义字段5//User defined field 5
  char custom5[MAX_CUSTOM_LEN + 1];
  float temperature;

  int phase;
  int freq;
  float latitude;
  float longitude;
} auto_read_output;

typedef struct report_option
{
  char ip[64];
  int port;
  int enable;
} report_option;

typedef struct device_config
{
  char name[64];
  int wifi_available;
  int g4_available;
  int wg_available;
  int supernet_available;
  int ant_count;
  int trigger_count;
  int relay_count;
  int di;    // device type
  int proto; // 0-uvp 1-p218 2-r2000
} device_config;

/**
 * 自动模式数据监听callback/Automatic mode data monitoring callback
 */
typedef void(__declspec(dllexport) * auto_read_callback)(auto_read_output);

typedef struct testCallback
{
  int (*read)(unsigned char*, size_t);
} testCallback;

/**
 * @brief callback called when notify frame received
 */
typedef void (*notify_callback_heartbeat)(char*);

/**
 * @brief callback called when gpo status changed
 */
typedef void (*notify_callback_gpo)(int, int);

/**
 * @brief 表示扫描到的读写器设备/scanned reader device
 *
 * 主要是扫描设备回调中使用<p>
 * It is mainly used for scanning device callbacks
 */
typedef struct rfid_device
{
  /// 设备IP地址/Device IP address
  char ip[64];
  /// tcp/udp端口/tcp/udp port
  int port;
  /// 读写器型号/Reader model
  char model[128];
  /// 读写器ID/ID
  char id[64];
  /// 485地址/rs485 address
  int rs485_address;
  // 232波特率/rs232 baud rate
  int rs232_baud;
  /// 485波特率/rs485 baud rate
  int rs485_baud;
  int ti;
  int proto;
} rfid_device;

/// 设备扫描回调函数/Device scan callback
typedef void (*rfid_device_scan_callback)(rfid_device);

/// 继电器自动控制参数/Relay automatic control parameters
typedef struct relay_option
{
  /**
   * @brief 继电器的用途/Use of Relays
   *
   * - 0不自动控制继电器
   * - 1当读到标签时，吸合继电器,
   * - 2当读到满足报警条件的标签时，吸合继电器<p>
   *
   * - 0 disable
   * - 1 When the label is read, pull in the relay.
   * - 2 When a label that meets the alarm condition is read, pull in the relay
   *
   */
  int tp;
  /// 表示闭合超时时间，单位：秒/the closure timeout time(second)
  int timeout;
  /// 天线位图，Bit[15:0]对应天线1-16，1:启用，0:不限制/antenna bitmap, Bit[15:0] for ant 1-16, 1=enabled, 0=no limit
  int antenna_bitmap;
  /// 触发逻辑，0:或(OR)，1:与(AND)/trigger logic, 0=OR, 1=AND
  int trigger_logic;
} relay_option;

typedef struct auto_reboot_option
{
  int enable;
  int format;
  int time;
} auto_reboot_option;

/// 工作模式/work mode
typedef enum work_mode
{
  /// 命令模式/command mode
  command_mode,
  /**
   * 自动模式（连续读卡）：读写器持续地自动读卡。<p>
   * Automatic mode (continuous card reading): The reader will continuously and automatically read the card.
   */
  auto_mode,
  /**
   * 自动模式（开关触发读卡）：当读写器的触发IO满足触发条件时，读一段时间的卡。<p>
   * Automatic mode (reading when triggered) : when the trigger IO of the reader meets the trigger condition,
   * read the card for a period of time.
   */
  trigger_mode,
  /**
   * 自动模式（双向触发读卡）：当读写器的触发IO1被触发时，开始读卡，触发IO2被触发时，停止读卡。或者，读写器的触发IO2被触发时，开始读卡，触发IO1被触发时，停止读卡。触发条件和时间可通过2FH命令设置。<p>
   *
   * Automatic mode (reading when bidirectional trigger is triggered ):When the reader's trigger IO1 is triggered,
   *  reading will be started, and when the trigger IO2 is triggered, reading will be stopped. or, conversely. Trigger
   * conditions and timeout can be set by the 2FH command
   */
  bidirection_trigger_mode,
} work_mode;

/// 频率区域/frequency region
typedef enum freq_region
{
  /// FCC
  region_usa,
  /// ETSI
  region_eu,
  /// CHN 840～845
  region_cn_900,
  /// USER DEFINED
  region_user,
  /// 920～925
  region_cn_800,
  region_korea,
  region_ce_low,
  // region_japan_1_no_lbt_4_fre
  region_japan_1,
  // region_japan_2_lbt_6_fre
  region_japan_2,
  // region_japan_3_no_lbt_19_fre,max_power_24_d_bm
  region_japan_3,
  region_ce_high,
  region_hk,
  region_taiwan,
  region_malaysia,
  region_southafrica,
  region_brazil,
  region_thailand,
  region_singapore,
  region_australia,
  region_india,
  region_uruguay,
  region_vietnam,
  region_israel,
  region_philippines,
  region_indonesia,
  region_new_zealand,
  region_peru,
  region_russia,
  region_full
} freq_region;

/**
 * @brief 自定义频率设置方式/Custom frequency Settings mode
 */
typedef enum freq_setting_type
{
  /// 自定义频点：完全自定义 / Custom Frequency Points: Fully custom
  freq_setting_user,
  /// 区域内自定义频点 / Customize frequency points within a region
  freq_setting_in_region,
  /// 区域内自动全频 / Automatic full frequency in the region
  freq_setting_in_region_auto,
} freq_setting_type;

/**
 * @brief 频率设置参数/Frequency setting parameter
 *
 * 根据不同的 @ref freq_setting_type 来实现不同的频率设置方式.<br>
 * Depending on the @ref freq_setting_type, different frequency Settings are implemented.
 *
 * - freq_setting_user
 *
 *  本模式下，region固定为region_user，freq_space,freq_count,start_freq有效<br>
 *  In this mode,region is fixed as region_user. freq_space,freq_count,start_freq are valid.
 *
 * - freq_setting_in_region
 *
 *  本模式下，region，start_freq_seq，end_freq_seq字段有效<br>
 *
 *  In this mode, region，start_freq_seq，end_freq_seq are valid.
 *
 * - freq_setting_in_region_auto
 *
 *  本模式下，仅region字段有效<br>
 *  In this mode, only region，is valid.
 */
typedef struct frequency_option
{
  /// 频段 region
  freq_region region;
  /// 频点间隔 FreqSpace，频点间隔 = FreqSpace * 10KHz。<br>
  ///  frequency points gap (unit is 10KHz)
  int freq_space;
  /**
   * @brief 起始频点/Start frequency point
   *
   * @details
   *
   * FCC范围0-52，ETSI范围0-6，CHN范围0-10， 具体请参考一下对应表<p>
   *
   * FCC Range 0-52, ETSI Range 0-6, CHN Range 0-10, Please refer to the corresponding table for details<p>
   *
   - FCC

    频点/frequency point|频率/frequency
    ------------- | -------------
    0	      |  865.00 MHz
    1	      |  865.50 MHz
    2	      |  866.00 MHz
    3	      |  866.50 MHz
    4	      |  867.00 MHz
    5	      |  867.50 MHz
    6	      |  868.00 MHz

    - ETSI

    频点/frequency point|频率/frequency|频点/frequency point|频率/frequency|频点/frequency point|频率/frequency|
    ------------- | -------| ------------- | -------|------------- | -------|
    0	        |902.00 MHz|20	|912.00 MHz|40	|922.00 MHz|
    1	        |902.50 MHz|21	|912.50 MHz|41	|922.50 MHz|
    2	        |903.00 MHz|22	|913.00 MHz|42	|923.00 MHz|
    3	        |903.50 MHz|23	|913.50 MHz|43	|923.50 MHz|
    4	        |904.00 MHz|24	|914.00 MHz|44	|924.00 MHz|
    5	        |904.50 MHz|25	|914.50 MHz|45	|924.50 MHz|
    6	        |905.00 MHz|26	|915.00 MHz|46	|925.00 MHz|
    7	        |905.50 MHz|27	|915.50 MHz|47	|925.50 MHz|
    8	        |906.00 MHz|28	|916.00 MHz|48	|926.00 MHz|
    9	        |906.50 MHz|29	|916.50 MHz|49	|926.50 MHz|
    10	        |907.00 MHz|30	|917.00 MHz|50	|927.00 MHz|
    11	        |907.50 MHz|31	|917.50 MHz|51	|927.50 MHz|
    12	        |908.00 MHz|32	|918.00 MHz|52	|928.00 MHz|
    13	        |908.50 MHz|33	|918.50 MHz|-   |-         |
    14	        |909.00 MHz|34	|919.00 MHz|-   |-         |
    15	        |909.50 MHz|35	|919.50 MHz|-   |-         |
    16	        |910.00 MHz|36	|920.00 MHz|-   |-         |
    17	        |910.50 MHz|37	|920.50 MHz|-   |-         |
    18	        |911.00 MHz|38	|921.00 MHz|-   |-         |
    19	        |911.05 MHz|39	|921.50 MHz|-   |-         |

    - CHN

       频点/frequency point|频率/frequency
    ------------- | -------------
    0	|920.00 MHz|
    1	|920.50 MHz|
    2	|921.00 MHz|
    3	|921.50 MHz|
    4	|922.00 MHz|
    5	|922.50 MHz|
    6	|923.00 MHz|
    7	|923.50 MHz|
    8	|924.00 MHz|
    9	|924.50 MHz|
    10	|925.00 MHz|
    *
   */
  int start_freq_seq;
  /// 结束频点。FCC范围0-52，ETSI范围0-6，CHN范围0-10。<br>
  /// end frequency point: FCC Range 0-52, ETSI Range 0-6, CHN Range 0-10
  int end_freq_seq;
  /// 频点数量 FreqQuantity
  int freq_count;
  /// 起始频率 StartFreq 单位为 KHz/ start frequency(unit KHz)
  int start_freq;
  /// 设置模式/ setting mode
  freq_setting_type setting_type;
} frequency_option;

/**
 * @brief 选择标签算法选项/ label algorithm option
 */
typedef struct tag_algorithm
{
  /**
   * 是否打开少量标签识别算法，0--否，1--是。仅支持部分模块<br>
   * Whether to open a small number of tag recognition algorithm, 0-- no, 1-- yes. Only some modules are supported
   */
  int less_tag;
  /**
   * 是否打开快速读取EPC＋TID+USER功能，0--否，1--是<br>
   * Whether to enable quick read EPC+TID+USER function, 0-- No, 1-- Yes
   */
  int fast_tid;
  /**
   * 是否打开快速天线切换功能，0--否，1--是<br>
   * Whether to turn on fast antenna switch, 0-- no, 1-- yes
   */
  int fast_ant_switch;
} tag_algorithm;

/**
 * @brief 标签过滤器/Label filter
 */
typedef struct filter
{
  /**
   *  过滤器的用途。
   *
   *  - 0 - 仅输出匹配的标签；
   *  - 1 - 输出所有标签，当识别到匹配的标签时，如果开启了自动控制继电器功能，则会触发继电器工作。<br>
   *
   * Purpose of the filter
   *
   * - 0 - Only matched labels are output
   * - 1 - all labels are output.
   */
  int enable;
  /// 掩码长度/Mask length
  int len;
  /// 掩码地址/ Mask Address
  int addr;
  /// 掩码数据/Mask Data
  unsigned char data[128];
} filter;

/**
 * @brief 自动模式下读取标签类型/The label type which read in automatic mode
 */
typedef struct tag_type_option
{
  /**
   * ISO18000-6B标签类型：0 - 不读取， 1 - 读取<br>
   *ISO18000-6B Tag Type: 0- do not read, 1 - read
   */
  int enable6c;
  /**
   * ISO18000-6C标签类型：0 - 不读取， 1 - 读取<br>
   * ISO18000-6C tag type: 0- do not read, 1 - read
   */
  int enable6b;

  int enable_temperature;
} tag_type_option;

/**
 * @brief 网络参数/network parameter
 */
typedef struct net_param
{
  /// IP
  char* ip;
  /// 子网掩码/subnet mask
  char* mask;
  /// 网关/gateway
  char* gw;
  /// 端口/port
  int port;
  /// DNS服务器地址/DNS server address (可选/optional)
  char* dns;
} net_param;

/**
 * @brief MQTT参数/MQTT parameter
 */
typedef struct mqtt_param
{
  /// 是否开启MQTT。0--不开启，1--开启
  int enable;
  /// 连接模式 0 rj45 1 wifi
  int connect_mode;
  /// MQTT服务器地址/MQTT
  char* ip;
  /// MQTT服务器端口/MQTT port
  int port;
  /// MQTT用户名/MQTT username
  char* username;
  /// MQTT密码/MQTT password
  char* password;
  /// 心跳包间隔/heartbeat interval
  unsigned char interval;
} mqtt_param;

typedef struct
{
  int enabled;
  const char* host;
  int port;
  const char* username;
  const char* password;
  const char* topic;
} simple_mqtt_config;

/**
 * @brief 自动上报格式字段标志/Automatic report format field flags
 *
 */
enum output_format_flags
{
  format_flag_epc = 1 << 0,
  format_flag_tid = 1 << 1,
  format_flag_user = 1 << 2,
  format_flag_rssi = 1 << 3,
  format_flag_ts = 1 << 4,
  format_flag_tag_type = 1 << 5,
  format_flag_ip = 1 << 6,
  format_flag_ant = 1 << 7,
  format_flag_custom4 = 1 << 26,
  format_flag_custom3 = 1 << 27,
  format_flag_custom2 = 1 << 28,
  format_flag_custom1 = 1 << 29,
  format_flag_custom0 = 1 << 30,
};

/**
 * @brief 自动模式上报格式选项/auto output data format settings
 */
typedef struct output_format
{
  /**
   * @brief 自动上报格式位图/format bitmap
   *
   * @details
   *
   * - 检查是否支持某个字段/checking flag
   *
   *@code
   * if(format.format & format_flag_ant) {
   *    printf("ant field enable");
   * }
   *@endcode
   *
   * - 设置某个字段/setting flag
   *
   * @code
   *   format.format = format.format | format_flag_ant | format_flag_epc |
   *                format_flag_ip | format_flag_tid;
   *
   *@endcode
   */
  int format;
  /// TID区起始地址/tid address
  int tid_addr;
  /// TID区长度/tid length
  int tid_len;
  /// USER区起始地址/ user address
  int user_addr;
  /// USER区长度/ user length
  int user_len;
} output_format;

/**
 * @brief 韦根设置参数/wiegand parameter
 */
typedef struct wg_option
{
  /// 韦根脉冲宽度。单位：10us / pulse width(10us)
  int width;
  /// 韦根脉冲间隔。单位：10us / pulse interval(10us
  int interval;
} wg_option;

/**
 * @brief 触发条件参数/trigger condition parameter
 */
typedef struct trigger_condition
{
  /// 触发电平/ trigger electrical level
  electrical_level level;
  /// 触发持续时间 / trigger duration
  int duration;
} trigger_condition;

/**
 * @brief 数据自动上报模式/auto output data report mode
 */
typedef enum report_type
{
  /**
   * 立即上报：读写器盘点一轮，上报一轮<br>
   * report right now: report after a round of inventory
   */
  report_now,
  /**
   * 定时上报（所有标签）：读写器在定时时间内进行盘点，定时时间到时将这段时间内盘点到的标签上报。<br>
   * report Periodically(all labels): inventory for a period and then report all labels read in this period
   */
  report_periodical1,
  /**
   * 定时上报（单标签）：针对单张标签的定时上报。例如上报的时间间隔设置为5秒，在第1秒的时候读到标签A，会立即上报该标签，但是往后的5秒时间内再次读到标签A时，不会再次上报。当时间超过5秒以后，再次识别到标签A，会立即上报。<br>
   * Timed reporting (single label): Timed reporting for a single label. For example, if the reporting interval is set
   * to 5 seconds, when tag A is read in the first second, the tag will be reported immediately, but if tag A is read
   * again in the next 5 seconds, it will not be reported again. When the time exceeds 5 seconds, tag A is recognized
   * again, and it will be reported immediately.
   */
  report_periodical2,
  /**
   *Modbus上报：此方式用于modbus协议 <br>
   * Modbus report: This method is used for modbus protocol.
   */
  report_modbus,
  /**
   * 实时上报：读写器在盘点过程中，识别到标签后实时上报标签的EPC或者UID。<br>
   * Real-time reporting: During the inventory process, the reader will report the EPC or UID of the tag in real time
   * after identifying the tag.
   */
  report_realtime,

  /**
   * 单标签模式：读写器在定时时间内进行盘点，定时时间到时将这段时间内盘点到的标签里盘点次数最多的一张标签上报。<br>
   * Single-label mode: The reader performs inventory for a period of time and reports the tag with the highest number of reads in this period.
   */
  report_single_most,
} report_type;

/**
 * @brief 自动上报条件设置参数
 */
typedef struct report_condition
{
  /// 上报模式/reporting mode
  report_type rtype;
  /// 上报的时间间隔/reporting interval. Unit: seconds
  int interval;
} report_condition;

/**
 * @brief 心跳包设置参数
 */
typedef struct heartbeat_option
{
  /**
   * 是否开启心跳包。0--不开启，1--开启
   *
   * whether to open the heartbeat packet. 0--not open, 1--open.
   */
  int enable;
  /// 心跳包间隔/heartbeat packet interval.
  int interval;
  /**
   * 最长24个字节，心跳数据的ASCII码
   *
   *The maximum length of 24 bytes, the ASCII code of the heartbeat data
   */
  char* message;
} heartbeat_option;

/**
 * @brief 标签类型/rfid tag type
 */
typedef enum tag_type
{
  /// 6c
  tag6c,
  /// 6b
  tag6b
} tag_type;

typedef enum hf_tag_type
{
  hf_a,
  hf_b,
  hf_f,
  hf_v,
} hf_tag_type;

/**
 * @brief 标签的区域/memory bank of tag
 */
typedef enum memory_bank
{
  /// 密码区/password
  memory_bank_password,
  /// EPC
  memory_bank_epc,
  /// TID
  memory_bank_tid,
  /// USER
  memory_bank_user,
} memory_bank;

/**
 * @brief 一次标签读取的结果/The result of a reading
 *
 * 结果包含了epc号以及读取的数据（EPC，TID，USER中的某个区的数据，取决于读取时的参数）
 *
 * The result contains the EPC and the data that was read (EPC, TID, User, depending on the parameters at the
 * time of reading)
 */
typedef struct tag
{
  /// EPC
  unsigned char id[MAX_EPC_LEN + 1];
  /// EPC长度/EPC length
  int len;
  /// 读取的数据/data which read
  unsigned char data[MAX_USER_LEN + 1];
  /// 数据长度/data length
  int dataLen;
  /// 标签类型/tag type 0--6C，1--6B
  tag_type tagType;
  double temperature;
  int ant;

  int rssi;
  int phase;
  int frequency;
  int read_count;
  int cost;

  /// 读取的tid数据/tid data which read
  unsigned char tid[MAX_USER_LEN + 1];
  /// tid数据长度/data length
  int tidLen;

  /// 读取的user数据/user data which read
  unsigned char user[MAX_USER_LEN + 1];
  /// tid数据长度/data length
  int userLen;

  /// 读取的password数据/password data which read
  unsigned char password[MAX_USER_LEN + 1];
  /// password数据长度/data length
  int passwordLen;
} tag;

typedef void (*on_tag_callback)(tag);

typedef struct hf_tag
{
  /// uid
  unsigned char uid[MAX_HF_UID_LEN + 1];
  /// uid length
  int uid_len;
  /// tag type
  hf_tag_type tagType;
} hf_tag;

/**
 * @brief 低频标签结构体
 */
typedef struct low_freq_tag
{
  /// 标签类型 (0x96 = ISO11784/85)
  unsigned char tag_type;
  /// 标签ID内容
  unsigned char *id;
  /// 标签ID长度
  int id_len;
} low_freq_tag;

typedef void (*tag_read_callback)(tag);

/**
 * @brief 锁定标签的区/Memory bank for locking
 */
typedef enum lock_memory_bank
{
  /// USER
  lock_memory_bank_user,
  /// TID
  lock_memory_bank_tid,
  /// EPC
  lock_memory_bank_epc,
  /// 访问密码区/access password memory bank
  lock_memory_bank_access_password,
  /// 销毁密码区/kill password memory bank
  lock_memory_bank_kill_password,
} lock_memory_bank;

/**
 * @brief 锁定类型/locking type
 */
typedef enum lock_type
{
  /// 开放/open
  lock_type_open,
  /// 锁定/lock
  lock_type_lock,
  /// 永久开放/open forever
  lock_type_open_forever,
  /// 永久锁定/lock forever
  lock_type_lock_forever,
} lock_type;

/**
 *@brief  wifi参数/wifi parameters
 */
typedef struct wifi_param
{
  /**
   *@brief wifi运行模式/wifi running mode
   *
   * 0--UDP Server，1--TCP Server
   */
  int mode;
  /// IP xxx.xxx.xxx.xxx
  char ip[MAX_IP_LEN + 1];
  /// 子网掩码/subnet mask
  char mask[MAX_IP_LEN + 1];
  /// 网关/gateway
  char gw[MAX_IP_LEN + 1];
  /// 通讯端口/communication port
  int port;
  /// wifi ssid
  char name[MAX_WIFI_SSID_LEN + 1];
  /// wifi密码/wifi password
  char password[MAX_WIFI_PASSWORD_LEN + 1];
  char remote_ip[MAX_IP_LEN + 1];
} wifi_param;

/**
 *@brief 超级网口配置信息/ TTL to Ethernet Modules parameters
 */
typedef struct super_net_if_param
{
  /**
   *@brief 运行模式/running mode
   *
   * 0--UDP Server，1--TCP Server
   */
  int mode;
  /// IP
  char* ip;
  /// 子网掩码/subnet mask
  char* mask;
  /// 网关/gateway
  char* gw;
  /// 通讯端口/communication port
  int port;
  char* remote_ip;
} super_net_if_param;

/**
 * @brief gsm网络参数/gsm4/5g parameter
 */
typedef struct four_g_param
{
  /**
   *@brief 运行模式/running mode
   *
   * 0--UDP Client，1--TCP Client
   */
  int mode;
  /// 通讯端口/communication port
  int server_port;
  /// IP
  char* server_ip;
} four_g_param;

/**
 * @brief USB模式配置/USB mode configuration
 */
typedef struct usb_mode_config
{
  /// USB工作模式/USB working mode
  /// 0x00: U盘模式/U-disk mode
  /// 0x01: 通讯模式/Communication mode
  /// 0x02: 仿真键盘模式/Keyboard emulation mode
  int mode;
  /// 仿真键盘模式下的回车配置/Enter key configuration in keyboard emulation mode
  /// 0x00: 数据输出无回车符/No enter key
  /// 0x01: 每段数据输出带回车符/With enter key
  int enter_mode;
} usb_mode_config;

typedef void (*disconnect_callback)();

/// 数据监控回调函数/Data monitoring callback function
typedef void (*data_monitor_callback)(unsigned char*, int, int);

#ifdef __cplusplus
extern "C"
{
#endif
  /**
   * @brief 暂定当前线程ms毫秒/pauses the current thread for ms Milliseconds
   * @param ms
   */
  __declspec(dllexport) void sleep_ms(int ms);

  /**
   * @brief 将字节数组转为16进制字符串/Converts a byte array to a hexadecimal string
   *
   * @param data 字节数组/byte array
   * @param len 字节数组长度/byte array length
   * @param [out] hex  输入/输出缓冲/input-output buffer
   */
  __declspec(dllexport) void to_hex_str(unsigned char* data, int len, char* hex);

  /**
   * @brief connect to reader via tcp/udp
   * @param ip ip
   * @param port port
   * @param protocol udp/tcp
   * @param [out] handle  connection handle
   * @return
   */
  __declspec(dllexport) int connect_net(char* ip, int port, transport_protocol protocol, int timeout, int* handle);

  /**
   * @brief 连接P32读写器(网络)/connect to P32 reader via tcp/udp
   * @param ip ip地址/ip address
   * @param port 端口/port
   * @param protocol 传输协议/transport protocol (udp/tcp)
   * @param [out] handle 连接句柄/connection handle
   * @return 错误码/error code
   */
  __declspec(dllexport) int connect_net_p32(char* ip, int port, transport_protocol protocol, int* handle);

  /**
   * @brief connect to reader via rs232/rs485
   * @param bd
   * @param address rs485 address
   * @param [out] handle
   * @return
   */
  __declspec(dllexport) int connect_rs(char*, enum baud bd, int address, int timeout, int* handle);

  /**
   * @brief connect to reader via rs232/rs485 with raw int baud rate (bypasses enum baud restriction)
   */
  __declspec(dllexport) int connect_rs_int(char* port, int baud, int address, int timeout, int* handle);

  /**
   * @brief 连接P32读写器(串口)/connect to P32 reader via rs232/rs485
   * @param port 串口号/serial port name
   * @param bd 波特率/baud rate
   * @param address RS485地址/rs485 address
   * @param [out] handle 连接句柄/connection handle
   * @return 错误码/error code
   */
  __declspec(dllexport) int connect_rs_p32(char*, enum baud bd, int address, int* handle);

  /**
   * @brief connect to P32 reader via rs232 with raw int baud rate (bypasses enum baud restriction)
   */
  __declspec(dllexport) int connect_rs_p32_int(char* port, int baud, int address, int* handle);

  /**
   * @brief disconnect from reader
   * @param handle
   */
  __declspec(dllexport) void disconnect(int handle);

  /**
   * @brief 连接模拟读写器（用于测试，无需真实设备）/ Connect to a mock reader for testing (no real device needed)
   *
   * 创建本地 mock server 并连接，返回的 handle 与真实设备连接完全兼容，
   * 可用于所有 reader API。调用 disconnect(handle) 即可清理 mock server。
   *
   * Creates a local mock server and connects to it. The returned handle is
   * fully compatible with all reader APIs. Call disconnect(handle) to clean up.
   *
   * @param port 端口号 / Port number (e.g. 26790)
   * @param [out] handle 输出连接句柄 / Output connection handle
   * @return 错误码，0表示成功 / Error code, 0 for success
   *
   * @code
   * int handle;
   * int ret = connect_mock(26790, &handle);
   * if (ret == err_ok) {
   *     // use handle with any reader API...
   *     disconnect(handle);
   * }
   * @endcode
   */
  __declspec(dllexport) int connect_mock(int port, int* handle);

  /**
   * @brief get reader version information
   * @param handle
   * @param [out] version  version info
   *
   * @warning you should free the out version info memory: c_free(version)
   *
   * @code
   * char version[128];
   * get_version(handle, version);
   *
   * @endcode
   *
   * @return
   */
  __declspec(dllexport) int get_version(int handle, char* version);

  /**
   * @brief get baud rate of the port(rs232 or rs485)
   * @param handle
   * @param p port rs232/rs485
   * @param [out] b  baud rate
   * @return
   */
  __declspec(dllexport) int get_baud(int handle, port p, baud* b);

  /**
   * @brief set baud rate of the port(rs232 or rs485)
   * @param handle
   * @param p port rs232/rs485
   * @param b baud rate
   * @return
   */
  __declspec(dllexport) int set_baud(int handle, port p, baud b);

  /**
   * @brief get rs485 address
   * @param handle
   * @param [out] address  485 address
   * @return
   */
  __declspec(dllexport) int get_485_address(int handle, int* address);

  /**
   *@brief set rs485 address
   * @param handle
   * @param address 485 address
   * @return
   */
  __declspec(dllexport) int set_485_address(int handle, int address);

  /**
   * @brief change status of the relay
   * @param handle
   * @param relay relay number
   * @param status status of relay
   * @return
   */
  __declspec(dllexport) int set_relay_status(int handle, int relay, relay_status status);

  /**
   * @brief get status of the relay
   * @param handle
   * @param relay relay number
   * @param [out] status  status of relay
   * @return
   */
  __declspec(dllexport) int get_relay_status(int handle, int relay, relay_status* status);

  /**
   * @brief 获取所有继电器状态/get status of all relays
   * @param handle 连接句柄/connection handle
   * @param [out] status 继电器状态数组/relay status array
   * @param [out] num 继电器数量/relay count
   * @return 错误码/error code
   */
  __declspec(dllexport) int get_relay_status_all(int handle, relay_status* status, int* num);

  /**
   * @brief 设置所有继电器状态/set status of all relays
   * @param handle 连接句柄/connection handle
   * @param status 继电器状态数组/relay status array
   * @param num 继电器数量/relay count
   * @return 错误码/error code
   */
  __declspec(dllexport) int set_relay_status_all(int handle, relay_status* status, int num);

  /**
   * @brief get gpi status
   * @param handle
   * @param gpi gpi number
   * @param [out] status 0 low level 1 high level
   * @return
   */
  __declspec(dllexport) int get_gpi_status(int handle, int gpi, int* status);

  /**
   * @brief 设置GPI状态/set gpi status
   * @param handle 连接句柄/connection handle
   * @param gpi GPI编号/gpi number
   * @param status 状态值/status value (0 low level, 1 high level)
   * @return 错误码/error code
   */
  __declspec(dllexport) int set_gpi_status(int handle, int gpi, int status);

  /**
   * @brief get relay count  of the reader
   * @param handle
   * @param [out] number  relay count
   * @return
   */
  __declspec(dllexport) int get_relay_number(int handle, int* number);

  /**
   *@brief 获取继电器自动控制参数/get relay control parameter
   * @param handle
   * @param relay 继电器编号/relay number
   * @param [out] option  控制参数/controls parameter
   * @return
   */
  __declspec(dllexport) int get_relay_option(int handle, int relay, relay_option* option);

  /**
   * @brief 设置继电器自动控制参数/set relay control parameter
   * @param handle
   * @param relay 继电器编号/relay number
   * @param option 控制参数/controls parameter
   * @return
   */
  __declspec(dllexport) int set_relay_option(int handle, int relay, relay_option option);

  /**
   * @brief 获取蜂鸣器状态/Gets the buzzer status
   * @param handle 连接句柄
   * @param [out] status   蜂鸣器状态: 0 开启 1 关闭 /  Buzzer status: 0 on 1 off
   * @return
   */
  __declspec(dllexport) int get_buzz(int handle, int* status);

  /**
   * @brief 设置蜂鸣器状态/sets the buzzer status
   * @param handle 连接句柄
   * @param status [in] 蜂鸣器状态: 0 开启 1 关闭/  Buzzer status: 0 on 1 off
   * @return
   */
  __declspec(dllexport) int set_buzz(int handle, int status);

  /**
   * @brief R2000模块设置蜂鸣器模式/set buzzer mode for R2000 module
   * @param handle 连接句柄/connection handle
   * @param status 蜂鸣器模式/buzzer mode
   *        0 - 安静模式/quiet mode
   *        1 - 每次盘点响一声/beep once per inventory
   *        2 - 每次读到标签响一声/beep once per tag read
   *        3 - 保留/reserved
   * @return 错误码/error code
   */
  __declspec(dllexport) int r2000_set_buzz(int handle, int status);

  /**
   * @brief 设置自动读取15693高频标签的单块数据长度/set auto read block size for 15693 tags
   * @param handle 连接句柄
   * @param [in] blockSize 块大小: 0x04表示4字节/块, 0x08表示8字节/块
   * @return 错误码
   */
  __declspec(dllexport) int set_15693_auto_read_block_size(int handle, int blockSize);

  /**
   * @brief 获取自动读取15693高频标签的单块数据长度/get auto read block size for 15693 tags
   * @param handle 连接句柄
   * @param [out] blockSize 块大小: 0x04表示4字节/块, 0x08表示8字节/块
   * @return 错误码
   */
  __declspec(dllexport) int get_15693_auto_read_block_size(int handle, int* blockSize);

  /**
   * @brief 配置Nandflash存储状态/configure Nandflash storage status
   * @param handle 连接句柄
   * @param enable 存储功能开关: 0-关闭, 1-打开
   * @param retention_time 标签保留时间(单位:秒),配置同一标签多少秒内不重复储存
   * @return 错误码
   */
  __declspec(dllexport) int set_nandflash_storage(int handle, unsigned char enable, unsigned char retention_time);

  /**
   * @brief 获取Nandflash存储状态/get Nandflash storage status
   * @param handle 连接句柄
   * @param [out] enable 存储功能开关: 0-关闭, 1-打开
   * @param [out] retention_time 标签保留时间(单位:秒)
   * @return 错误码
   */
  __declspec(dllexport) int get_nandflash_storage(int handle, unsigned char* enable, unsigned char* retention_time);

// M1卡密钥类型定义
#define M1_KEY_A 1
#define M1_KEY_B 2

  /**
   * @brief 读取M1卡扇区数据块
   *
   * @param id 设备ID
   * @param uid 卡片UID
   * @param uid_len UID长度
   * @param sector 扇区号
   * @param block_bitmap 位图，bit0-bit2数据块，bit3控制块
   * @param key_type 密钥类型：M1_KEY_A或M1_KEY_B
   * @param key 6字节密钥
   * @param out_bitmap 输出：返回的数据块bitmap
   * @param out_error_bitmap 输出：操作结果bitmap
   * @param out_data 输出：数据块数据缓冲区
   * @param out_data_len 输入：缓冲区大小，输出：实际数据长度
   * @return int 错误码，0表示成功
   */
  __declspec(dllexport) int read_m1_sector_blocks(int id, const unsigned char *uid, unsigned char uid_len,
                           unsigned char sector, unsigned char block_bitmap,
                           unsigned char key_type, const unsigned char *key,
                           unsigned char *out_bitmap, unsigned char *out_error_bitmap,
                           unsigned char *out_data, unsigned int *out_data_len);

  /**
   * @brief 写入M1卡扇区数据块
   *
   * @param id 设备ID
   * @param uid 卡片UID
   * @param uid_len UID长度
   * @param sector 扇区号
   * @param block_bitmap 位图，bit0-bit2数据块，bit3控制块
   * @param key_type 密钥类型：M1_KEY_A或M1_KEY_B
   * @param key 6字节密钥
   * @param data 写入数据，每块16字节
   * @param data_len 数据长度
   * @param out_result_bitmap 输出：操作结果bitmap
   * @return int 错误码，0表示成功
   */
  __declspec(dllexport) int write_m1_sector_blocks(int id, const unsigned char *uid, unsigned char uid_len,
                            unsigned char sector, unsigned char block_bitmap,
                            unsigned char key_type, const unsigned char *key,
                            const unsigned char *data, unsigned int data_len,
                            unsigned char *out_result_bitmap);

  /**
   * @brief 设置工作模式/set working mode
   * @param handle 连接句柄
   * @param [in] mode 工作模式/working mode
   * @return 错误码
   */
  __declspec(dllexport) int set_work_mode(int handle, work_mode mode);

  /**
   * @brief 临时设置工作模式/set working mode
   * @param handle 连接句柄
   * @param [in] mode 工作模式/working mode
   * @return 错误码
   */
  __declspec(dllexport) int set_work_mode_temp(int handle, work_mode mode);

  /**
   * @brief 获取当前工作模式/get working mode
   * @param handle 连接句柄
   * @param [out] mode   工作模式/working mode
   * @return 错误码
   */
  __declspec(dllexport) int get_work_mode(int handle, work_mode* mode);

  /**
   * @brief 设置天线功率/Set antenna power
   * @param handle 连接句柄
   * @param  [in] ant 天线号/Antenna  NO
   * @param [in] power 功率/power
   * @return 错误码
   */
  __declspec(dllexport) int set_power(int handle, int ant, int power);

  /**
   * @brief 设置多个天线功率/set power for multiple antennas
   * @param handle 连接句柄/connection handle
   * @param power 功率数组/power array
   * @param count 天线数量/antenna count
   * @return 错误码/error code
   */
  __declspec(dllexport) int set_powers(int handle, int* power, int count);

  /**
   * @brief 获取天线功率/Get antenna power
   * @param handle 连接句柄
   * @param ant 天线号/Antenna  NO
   * @param [out] power   功率/power
   * @return 错误码
   */
  __declspec(dllexport) int get_power(int handle, int ant, int* power);

  /**
   * @brief 获取多个天线功率/get power of multiple antennas
   * @param handle 连接句柄/connection handle
   * @param [out] power 功率数组指针/power array pointer
   * @param [out] count 天线数量/antenna count
   * @return 错误码/error code
   * @warning 使用完毕后需要释放内存/free memory after use
   */
  __declspec(dllexport) int get_powers(int handle, int** power, int* count);

  /**
   * @brief 设置模块频率区域/set rfid module frequency region
   * @param handle 连接句柄
   * @param  [out] region   频率区域/region
   * @return 错误码
   */
  __declspec(dllexport) int set_freq_region(int handle, freq_region region);

  /**
   * @brief 获取模块频率区域/get rfid module frequency region
   * @param handle 连接句柄
   * @param [in] region  频率区域/region
   * @return 错误码
   */
  __declspec(dllexport) int get_freq_region(int handle, freq_region* region);

  /**
   * @brief 自定义rfid模块频率参数/Customize RFID module frequency parameters
   * @param handle
   * @param option 频率参数/frequency parameter
   * @return
   */
  __declspec(dllexport) int set_user_freq(int handle, frequency_option option);

  /**
   * 获取rfid模块频率参数/get  frequency parameters of RFID module
   * @param handle
   * @param [out] option  频率参数/frequency parameter
   * @return
   */
  __declspec(dllexport) int get_user_freq(int handle, frequency_option* option);

  /**
   * @brief 获取自动模式下轮询的天线/Gets the polling antennas in automatic mode
   * @param handle 连接句柄
   * @param [out] ants   天线号数组/antenna NO.s
   * @param [out] count   天线个数/The number of antennas
   * @return 错误码
   */
  __declspec(dllexport) int get_inventory_ants(int handle, int* ants, int* count);

  /**
   * @brief 设置自动模式下轮询的天线/sets the polling antennas in automatic mode
   * @param handle 连接句柄
   * @param [in] ants 天线数组/antenna NO.s
   * @param [in] count 天线个数/The number of antennas
   * @return 错误码
   */
  __declspec(dllexport) int set_inventory_ants(int handle, const int* ants, int count);

  /**
   * @brief 恢复出厂设置/restore factory setting
   * @param handle 连接句柄
   * @return 错误码
   */
  __declspec(dllexport) int factory(int handle);

  /**
   * @brief 重启读写器/Restart
   * @param handle 连接句柄
   * @return 错误码
   */
  __declspec(dllexport) int reboot(int handle);

  /**
   * @brief 恢复wifi出厂设置/Restore WiFi factory Settings
   * @param handle 连接句柄
   * @return 错误码
   */
  __declspec(dllexport) int reset_wifi(int handle);

  /**
   * @brief 设置读写器时间/Set the reader time
   * @param handle 连接句柄
   * @param year 实际年-2000， 例如要设置2021年，需要传入2021-2000 = 21<br>
   * The actual year -2000, for example to set the year 2021, needs to pass in 2021-2000 = 21
   * @param month 月/month
   * @param day 日/day
   * @param hour 小时/hour
   * @param minute 分钟/minute
   * @param second 秒/second
   * @return 错误码
   */
  __declspec(dllexport) int set_time(int handle, int year, int month, int day, int hour, int minute, int second);

  /**
   * @brief 获取读写器时间/Gets the reader time
   *
   * @param [in]year 实际年等于该值+2000<br>The actual year is equal to this value plus 2000
   * @param [in]month 月/month
   * @param [in]day 日/day
   * @param [in]hour 小时/hour
   * @param [in]minute 分钟/minute
   * @param [in]second 秒/second
   * @return 错误码
   */
  __declspec(dllexport) int get_time(int handle, int* year, int* month, int* day, int* hour, int* minute, int* second);

  /**
   * @brief 设置系统时区/Set system timezone
   * @param handle 连接句柄
   * @param timezoneId 时区ID，如 "Asia/Shanghai", "America/New_York"
   *        最大长度63字节
   * @return 错误码
   */
  __declspec(dllexport) int set_timezone(int handle, const char* timezoneId);

  /**
   * @brief 获取系统时区/Get system timezone
   * @param handle 连接句柄
   * @param [out] timezoneId 时区ID缓冲区
   * @param [in] maxLen 缓冲区最大长度
   * @return 错误码
   */
  __declspec(dllexport) int get_timezone(int handle, char* timezoneId, int maxLen);

  /**
   * @brief 设置rfid标签算法相关参数/Set the RFID tag algorithm parameters
   * @param handle
   * @param algorithm 参数/parameter
   * @return
   */
  __declspec(dllexport) int set_tag_algorithm(int handle, tag_algorithm algorithm);

  /**
   * @brief 获取rfid标签算法相关参数/get the RFID tag algorithm parameters
   * @param handle
   * @param [out] algorithm  参数/parameter
   * @return
   */
  __declspec(dllexport) int get_tag_algorithm(int handle, tag_algorithm* algorithm);

  /**
   * @brief 设置标签过滤器/Set Label Filters
   *
   * @param handle
   * @param flt 过滤器参数/filter parameter
   * @return
   */
  __declspec(dllexport) int set_filter(int handle, filter flt);

  /**
   * @brief 获取标签过滤器/get Label Filters
   *
   * @param handle
   * @param [out] flt  过滤器参数/filter parameter
   * @return
   */
  __declspec(dllexport) int get_filter(int handle, filter* flt);

  /**
   * @brief 设置自动模式下读取标签类型/Sets the read label type in automatic mode
   * @param handle
   * @param option
   * @return
   */
  __declspec(dllexport) int set_tag_type_option(int handle, tag_type_option option);

  /**
   * @brief 获取自动模式下读取标签类型/gets the read label type in automatic mode
   * @param handle
   * @param [out] option
   * @return
   */
  __declspec(dllexport) int get_tag_type_option(int handle, tag_type_option* option);

  /**
   * @brief 获取网络参数/Get network parameters
   * @param handle
   * @param [out] param  网络参数/network parameter
   *
   * @warning 使用完param之后，请释放内存free_network_param(&param);<br>
   * After using param, free the memory with free_network_param(&amp;param)
   * @return
   */
  __declspec(dllexport) int get_network_param(int handle, net_param* param);

  /**
   * @brief 释放网络参数结构内存/Free network parameter structure memory
   * @param param
   */
  __declspec(dllexport) void free_network_param(net_param* param);

  /**
   * @brief 获取MQTT参数/get MQTT parameters
   * @param handle 连接句柄/connection handle
   * @param [out] param MQTT参数指针/MQTT parameter pointer
   * @return 错误码/error code
   * @warning 使用完毕后需要使用free_mqtt_param释放内存/free memory with free_mqtt_param after use
   */
  __declspec(dllexport) int get_mqtt_param(int handle, mqtt_param** param);

  /**
   * @brief 设置MQTT参数/set MQTT parameters
   * @param handle 连接句柄/connection handle
   * @param param MQTT参数/MQTT parameter
   * @return 错误码/error code
   */
  __declspec(dllexport) int set_mqtt_param(int handle, mqtt_param param);

  /**
   * @brief 释放MQTT参数内存/free MQTT parameter memory
   * @param param MQTT参数指针/MQTT parameter pointer
   */
  __declspec(dllexport) void free_mqtt_param(mqtt_param* param);

  /**
   * @brief 设置网络参数/set network parameters
   * @param handle
   * @param param 网络参数/network parameter
   * @return
   */
  __declspec(dllexport) int set_network_param(int handle, net_param param);

  /**
   * @brief 设置网络参数（带DNS）/set network parameters with DNS
   * @param handle
   * @param ip IP地址/IP address
   * @param mask 子网掩码/subnet mask
   * @param gw 网关/gateway
   * @param port 端口/port
   * @param dns DNS服务器地址/DNS server address (可为NULL/not required)
   * @return
   */
  __declspec(dllexport) int set_network_param_with_dns(int handle, char* ip, char* mask, char* gw, int port, char* dns);

  /**
  * @brief 获取mac地址/Get MAC address
  *
  * 基本用法
  * @code
  char *mac = "12:34:89:09:aa:bb";
  int ret = set_mac(id, mac);
  assert(ret == err_ok);

  mac = NULL;
  ret = get_mac(id, &mac);
  assert(ret == err_ok);
  assert(strcmp(mac, "12:34:89:09:aa:bb") == 0);

  c_free(mac);
  * @endcode
  *
  * 注意释放内存
  * @param handle
  * @param [out] mac
  * @return
  */
  __declspec(dllexport) int get_mac(int handle, char** mac);

  /**
   * 释放内存, free函数的包装<br>
   * Free memory, free function wrapper
   * @param pointer
   */
  __declspec(dllexport) void c_free(void* pointer);

  /**
   * @brief 释放字符串数组/Free string array
   * @param arr
   * @param size
   */
  __declspec(dllexport) void free_c_string_array(char** arr, int size);

  /**
   * @brief 设置mac地址/set mac address
   * @param handle
   * @param mac mac
   * @return
   */
  __declspec(dllexport) int set_mac(int handle, char* mac);

  /**
   * @brief 获取上报接口状态/get report port status
   * @param handle 连接句柄
   * @param p 接口/port
   * @param [out] status  0 - 未启用  1 - 已启用
   *
   * 0 - Not Enabled 1 - Enabled
   *
   * @return 错误码
   */
  __declspec(dllexport) int get_report_port_status(int handle, port p, int* status);

  /**
   * @brief 设置上报接口状态/set report port status
   * @param handle 连接句柄
   * @param p 接口/port
   * @param status  0 表示未启用  1 表示已启用
   *
   * 0 - Not Enabled 1 - Enabled
   * @return 错误码
   */
  __declspec(dllexport) int set_report_port_status(int handle, port p, int status);

  /**
   * @brief 设置自动输出格式/set auto output data format
   * @param handle
   * @param format 格式参数/format parameter
   * @return
   */
  __declspec(dllexport) int set_output_format(int handle, output_format format);

  /**
   * @brief 获取自动输出格式/get auto output data format
   * @param handle
   * @param [out] format  格式参数/format parameter
   * @return
   */
  __declspec(dllexport) int get_output_format(int handle, output_format* format);

  /**
   * @brief 设置自定义上报字段内容/set custom report content
   * @param handle
   * @param seq 字段序号/field number
   * @param content 内容/content
   * @return
   */
  __declspec(dllexport) int set_custom_report_content(int handle, int seq, char* content);

  /**
   * @brief 获取自定义上报内容/get custom report content
   * @param handle 连接句柄
   * @param seq 序号/field number
   * @param [out] content  上报内容/content
   * @return 错误码
   */
  __declspec(dllexport) int get_custom_report_content(int handle, int seq, char** content);

  /**
   * @brief 设置韦根参数/set wiegand parameter
   * @param handle
   * @param option 参数/parameter
   * @return
   */
  __declspec(dllexport) int set_wg_option(int handle, wg_option option);

  /**
   * @brief 获取韦根参数/get wiegand parameter
   * @param handle
   * @param [out] option  参数/parameter
   * @return
   */
  __declspec(dllexport) int get_wg_option(int handle, wg_option* option);

  /**
   *@brief 获取触发条件参数/get trigger condition param
   * @param handle
   * @param seq 触发器序号/trigger number
   * @param condition 参数/param
   * @return
   */
  __declspec(dllexport) int get_trigger_option(int handle, int seq, trigger_condition* condition);

  /**
   *@brief 设置触发条件参数/set trigger condition param
   * @param handle
   * @param seq 触发器序号/trigger number
   * @param [out] condition  参数/param
   * @return
   */
  __declspec(dllexport) int set_trigger_option(int handle, int seq, trigger_condition condition);

  /**
   * @brief 获取读写器ID/get reader id
   * @param handle
   * @param [out] id  id
   *
   * @warning free id after you will never use it any more c_free(id)
   * @return
   */
  __declspec(dllexport) int get_id(int handle, char** id);

  /**
   * @brief 设置读写器ID/set reader id
   * @param handle 连接句柄/connection handle
   * @param id 读写器ID/reader id
   * @return 错误码/error code
   */
  __declspec(dllexport) int set_id(int handle, char* id);

  /**
   * @brief 获取读写器名字/get reader name
   * @param handle
   * @param [out] mode  名字/name
   *
   * @warning free mode after you will never use it any more c_free(mode)
   *
   * @return
   */
  __declspec(dllexport) int get_reader_name(int handle, char** mode);

  /**
   *@brief 设置读写器名字/set reader name
   * @param handle
   * @param mode 名字/name
   * @return
   */
  __declspec(dllexport) int set_reader_name(int handle, char* mode);

  /**
   * @brief 设置自动上报条件设置参数/set auto output report condition param
   * @param handle
   * @param condition 条件参数/condition param
   * @return
   */
  __declspec(dllexport) int set_report_condition(int handle, report_condition condition);

  /**
   * @brief 获取自动上报条件设置参数/get auto output report condition param
   * @param handle
   * @param [out] condition  条件参数/condition param
   * @return
   */
  __declspec(dllexport) int get_report_condition(int handle, report_condition* condition);

  /**
   * @brief 设置心跳参数/set heartbeat parameter
   * @param handle
   * @param option 心跳参数/heartbeat parameter
   * @return
   */
  __declspec(dllexport) int set_heartbeat_option(int handle, heartbeat_option option);

  /**
   * @brief 获取心跳参数/get heartbeat parameter
   * @param handle
   * @param [out] option  心跳参数/heartbeat parameter
   *
   *  @warning free option after you will never use it any more with free_heartbeat_option(option)
   *
   * @return
   */
  __declspec(dllexport) int get_heartbeat_option(int handle, heartbeat_option* option);

  /**
   * @brief 释放心跳参数内存/release  heartbeat parameter memory
   * @param option
   */
  __declspec(dllexport) void free_heartbeat_option(heartbeat_option* option);

  /**
   * @brief 设置当前工作天线/Set the current working antenna
   * @param handle
   * @param ant 天线序号/The antenna number
   * @return
   */
  __declspec(dllexport) int set_work_ant(int handle, int ant);

  /**
   * @brief 获取当前工作天线/get current working antenna
   * @param handle 连接句柄/connection handle
   * @param [out] ant 天线序号/antenna number
   * @return 错误码/error code
   */
  __declspec(dllexport) int get_work_ant(int handle, int* ant);

  /**
   * @brief 获取读写器天线个数/Get the number of reader antennas
   * @param handle
   * @param [out] count  天线个数/The number of antennas
   * @return
   */
  __declspec(dllexport) int get_ant_count(int handle, int* count);

  /**
   * @brief 列出附近的标签/List nearby tags
   *
   * 基本用法参考首页.<p>
   * Refer to the home page for basic usage
   *
   * @note 当memory参数是epc时， addr,len参数无效，会列出epc<p>
   * When the memory parameter is EPC, the addr,len parameters are invalid and the EPC is listed
   *
   * @param handle
   * @param memory 指定需要列出的内存区/memory that need to be listed
   * @param addr 起始地址/start address
   * @param len 读取长度/length
   * @param password 访问密码/access password
   * @param passwordLen 密码长度/password length
   * @param [out] tags  tags
   * @param [out] count  标签个数/tag count
   *
   *  @warning free tags after you will never use it any more c_free(tags)
   * @return
   */
  __declspec(dllexport) int list6c(
      int handle,
      memory_bank memory,
      int addr,
      int len,
      unsigned char* password,
      int passwordLen,
      tag** tags,
      int* count);

  /**
   * @brief 列出附近的标签(回调版本)/list nearby tags (callback version)
   * @param handle 连接句柄/connection handle
   * @param memory 内存区/memory bank
   * @param addr 起始地址/start address
   * @param l 读取长度/length to read
   * @param password 访问密码/access password
   * @param password_len 密码长度/password length
   * @param cb 标签回调函数/tag callback function
   * @return 错误码/error code
   */
  __declspec(dllexport) int list6c_2(
      int handle,
      memory_bank memory,
      int addr,
      int l,
      unsigned char* password,
      int password_len,
      on_tag_callback cb);

  /**
   * @brief 读取标签数据(版本2)/read tag data (version 2)
   * @param handle 连接句柄/connection handle
   * @param memory 内存区/memory bank
   * @param addr 起始地址/start address
   * @param len 读取长度/length to read
   * @param epc 标签EPC/tag EPC
   * @param epcLen EPC长度/EPC length
   * @param password 访问密码/access password
   * @param passwordLen 密码长度/password length
   * @param [out] tags 标签数组指针/tag array pointer
   * @param [out] count 标签数量/tag count
   * @return 错误码/error code
   * @warning 使用完毕后需要释放标签内存/free tags memory after use
   */
  __declspec(dllexport) int read6c2(
      int handle,
      memory_bank memory,
      int addr,
      int len,
      unsigned char* epc,
      int epcLen,
      unsigned char* password,
      int passwordLen,
      tag** tags,
      int* count);

  /**
   *@brief 读取标签的特定数据/Read the specific data of the label
   *
   * 参考主页用法
   *
   * @param handle
   * @param memory 指定需要读取的内存区/memory that need to be read
   * @param addr 起始地址/start address
   * @param len 长度/length
   * @param epc 待读取的epc/The EPC to be read
   * @param epcLen epc长度/length
   * @param password 访问密码/access password
   * @param passwordLen 密码长度/password length
   * @param [out] data  读到的数据/data read
   * @param [out] count  数据长度/data length
   * @return
   */
  __declspec(dllexport) int read6c(
      int handle,
      memory_bank memory,
      int addr,
      int len,
      unsigned char* epc,
      int epcLen,
      unsigned char* password,
      int passwordLen,
      unsigned char* data,
      int* count);

  /**
   *@brief 往标签的特定区域写入数据/Writes data to a specific area of the label
   *
   *
   * @param handle
   * @param memory 指定需要写入的内存区/memory that need to be writen
   * @param addr 起始地址/start address
   * @param epc 待写入标签的epc/The EPC to be writen
   * @param epcLen epc长度/length
   * @param data 待写入数据/data to be writen
   * @param dataLen 数据长度/data length
   * @param password 访问密码/access password
   * @param passwordLen 密码长度/password length
   * @return
   */
  __declspec(dllexport) int write6c(
      int handle,
      memory_bank memory,
      int addr,
      unsigned char* epc,
      int epcLen,
      unsigned char* data,
      int dataLen,
      unsigned char* password,
      int passwordLen);

  /**
   * @brief 锁定标签/locking tag
   * @param handle
   * @param lockMemory 锁定区域/locking memory
   * @param lt 锁定类型/locking type
   * @param epc 待锁定标签epc/tag to be locked
   * @param epcLen epc长度/epc length
   * @param password 访问密码/access password
   * @param passwordLen 密码长度/password length
   * @return
   */
  __declspec(dllexport) int lock6c(
      int handle,
      lock_memory_bank lockMemory,
      lock_type lt,
      unsigned char* epc,
      int epcLen,
      unsigned char* password,
      int passwordLen);

  /**
   * @brief lock_u9_tag 锁定U9芯片标签
   * @param handle 连接句柄
   * @param lock_mode 锁定模式: 0=锁定所有U9标签, 1=锁定指定标签
   * @param epc 目标标签EPC
   * @param epc_len EPC长度
   * @return 错误码
   */
  __declspec(dllexport) int lock_u9_tag(int handle, unsigned char lock_mode, unsigned char* epc, int epc_len);

  /**
   * @brief lock_u9_tag_r2000 锁定U9芯片标签 (R2000协议 0x83，无参数)
   * @param handle 连接句柄
   * @return 错误码
   */
  __declspec(dllexport) int lock_u9_tag_r2000(int handle);

  /**
   *@brief 毁灭标签/killing tag
   * @param handle
   * @param epc 待毁灭标签/tag to be killed
   * @param epcLen 标签长度/epc length
   * @param killPassword 毁灭密码/kill password
   * @param passwordLen 密码长度/password length
   * @return
   */
  __declspec(dllexport) int kill6c(int handle, unsigned char* epc, int epcLen, unsigned char* killPassword, int passwordLen);

  /**
   * @brief 快速写epc/fast write epc
   * @param handle
   * @param oldEpc 旧的epc，如果这个参数不为空，则更新这个标签的epc，否则随机写一个epc。<p>
   * if oldEpc is null, it will write a random epc. or it will update the old epc to new epc.
   * @param oldEpcLen 旧epc长度/old epc length
   * @param newEpc 待写入epc/epc
   * @param newEpcLen epc长度/epc length
   * @param password 访问密码/access password
   * @param passwordLen 密码长度/password length
   * @return
   */
  __declspec(dllexport) int quick_write_epc(
      int handle,
      unsigned char* oldEpc,
      int oldEpcLen,
      unsigned char* newEpc,
      int newEpcLen,
      unsigned char* password,
      int passwordLen);

  /**
   * @brief 列出附近的6b标签/List nearby 6B tags
   * @param handle
   * @param [out] tags  tags
   * @param [out] count
   *
   *  @warning free tags after you will never use it any more c_free(tags)
   * @return
   */
  __declspec(dllexport) int list6b(int handle, tag** tags, int* count);

  /**
   * @brief 读取6b标签特定数据/read 6b data
   * @param handle
   * @param uid uid
   * @param uidLen uid长度/uid length
   * @param addr 起始地址/start address
   * @param len 待读取长度/data length to read
   * @param [out] data  读取数据/data
   * @param [out] count  数据长度/data length
   * @return
   */
  __declspec(dllexport) int read6b(int handle, unsigned char* uid, int uidLen, int addr, int len, unsigned char* data, int* count);

  /**
   * @brief 写入6b标签数据/write 6b data
   *
   * @param handle
   * @param uid uid
   * @param uidLen uid长度/uid length
   * @param addr 起始地址/start address
   * @param data 待写入数据/data to be write
   * @param dataLen 数据长度/data length
   * @return
   */
  __declspec(dllexport) int write6b(int handle, unsigned char* uid, int uidLen, int addr, unsigned char* data, int dataLen);

  /**
   * @brief 锁定6b标签/lock 6b tag
   * @param handle
   * @param uid uid
   * @param uidLen  uid长度/uid length
   * @param addr  起始地址/start length
   * @return
   */
  __declspec(dllexport) int lock6b(int handle, unsigned* uid, int uidLen, int addr);

  /**
   *@brief 设置标签报警器/set tag alarm
   * @param handle
   * @param flt 报警器参数/alarm parameter
   * @return
   */
  __declspec(dllexport) int set_alarm(int handle, filter flt);

  /**
   *@brief 获取标签报警器/get tag alarm
   * @param handle
   * @param [out] flt 报警器参数/alarm parameter
   * @return
   */
  __declspec(dllexport) int get_alarm(int handle, filter* flt);

  /**
   *@brief 设置快速读取TID功能/set fast tid
   * @param handle
   * @param enable 1 enable 0 disable
   * @return
   */
  __declspec(dllexport) int set_fast_tid(int handle, int enable);

  /**
   *@brief 获取快速读取TID功能/get fast tid
   * @param handle
   * @param [out] enable 1 enable 0 disable
   * @return
   */
  __declspec(dllexport) int get_fast_tid(int handle, int* enable);

  /**
   * @brief 获取触发器个数/Gets the number of triggers
   * @param handle
   * @param [out] number  触发器个数/number of trigger
   * @return
   */
  __declspec(dllexport) int get_trigger_number(int handle, int* number);

  /**
   * @brief 监听自动模式下读到的数据/Listen for data read in automatic mode
   *
   *
   * @param handle
   * @param callback
   * @return
   */
  __declspec(dllexport) int start_listen_auto_read(int handle, auto_read_callback callback);

  /**
   * @brief 同步监听自动模式下读到的数据(阻塞直到停止)/listen for data read in automatic mode (blocking until stopped)
   * @param handle 连接句柄/connection handle
   * @param callback 数据回调函数/data callback function, called when tag is read
   * @return 错误码/error code
   * @note 此函数会阻塞当前线程，直到调用stop_listen_auto_read停止监听/this function blocks current thread until stop_listen_auto_read is called
   */
  __declspec(dllexport) int start_listen_auto_read_sync(int handle, auto_read_callback callback);

  /**
   * @brief 设置断开连接回调函数/set disconnect callback function
   * @param handle 连接句柄/connection handle
   * @param callback 断开连接回调函数/disconnect callback function
   */
  __declspec(dllexport) void set_disconnect_callback(int handle, disconnect_callback callback);

  /**
   * @brief 停止监听数据/Stop listening for data
   * @param handle
   * @return
   */
  __declspec(dllexport) int stop_listen_auto_read(int handle);

  /**
   * @brief 监听自动模式下udp上报的数据/Listen to data reported by UDP in automatic mode.
   *
   * 部分型号读写器udp连接下需要用此函数监听
   *
   *  Some types of readers need to listen to this function under UDP connection
   *
   * @param ip 监听ip地址
   * @param port 监听端口
   * @param callback 有输出的时候会回调该函数
   * @return
   */
  __declspec(dllexport) int start_listen_udp_auto_read(char* ip, int port, auto_read_callback callback);

  /**
   * @brief 监听P32读写器UDP自动上报数据/listen to P32 reader UDP auto report data
   * @param port 监听端口/listen port
   * @param callback 数据回调函数/data callback function
   * @return 错误码/error code
   */
  __declspec(dllexport) int start_listen_udp_auto_read_p32(int port, auto_read_callback callback);

  /**
   * @brief 停止监听udp数据/stop listen auto output data
   * @param ip 监听ip地址/reader ip
   * @param port 监听端口/reader port
   * @return
   */
  __declspec(dllexport) int stop_listen_udp_auto_read(char* ip, int port);

  /**
   * @brief 停止监听P32读写器UDP自动上报数据/stop listening P32 reader UDP auto report data
   * @param port 监听端口/listen port
   * @return 错误码/error code
   */
  __declspec(dllexport) int stop_listen_udp_auto_read_p32(int port);

  /**
   * @brief P218读写器监听UDP自动上报数据/P218 reader listen to UDP auto report data
   * @param port 监听端口/listen port
   * @param callback 数据回调函数/data callback function
   * @return 错误码/error code
   */
  __declspec(dllexport) int start_listen_udp_auto_read_p218(int port, auto_read_callback callback);

  /**
   * @brief 停止P218读写器UDP自动上报监听/stop P218 reader UDP auto report listening
   * @param port 监听端口/listen port
   * @return 错误码/error code
   */
  __declspec(dllexport) int stop_listen_udp_auto_read_p218(int port);

  /**
   * @brief start to scan device
   *
   * scan device(must be in the same network) which is online, for example
   *
   * @code
   * void example_callback(rfid_device device)
   * {
   *      printf("device ip[%s] port[%d]\n", device.ip, device.port);
   * }
   *
   *
   * you should stop scanning also by yourself with @ref stop_device_scan
   *
   * @endcode
   *
   * @param callback
   * @return
   */
  __declspec(dllexport) int start_device_scan(rfid_device_scan_callback callback);

  /**
   * stop device scan request
   * @return
   */
  __declspec(dllexport) int stop_device_scan();

  /**
   * @brief 获取设备型号/get equipment mode
   * @param handle
   * @param [out] mode 型号编号/equipment mode
   * @return
   */
  __declspec(dllexport) int get_equipment_mode(int handle, int* mode);

  /**
   * @brief 查询读写器是否装配了wifi模块/get wifi module availability
   * @param handle
   * @param [out] available
   * @return
   */
  __declspec(dllexport) int is_wifi_available(int handle, int* available);

  /**
   * @brief get_modul_type
   * @param handle
   * @param [out] modul_type
   * @return
   */
  __declspec(dllexport) int get_modul_type(int handle, int* modul_type);

  /**
   * @brief 查询读写器是否装配了gsm4/5g模块/get gsm4g/5g module availability
   * @param handle
   * @param [out] available
   * @return
   */
  __declspec(dllexport) int is_4g_available(int handle, int* available);

  /**
   * @brief 查询读写器是否装配了韦根/get wiegand module availability
   * @param handle
   * @param [out] available
   * @return
   */
  __declspec(dllexport) int is_wg_available(int handle, int* available);

  /**
   * @brief 查询读写器是否装配了超级网口/get super network interface module availability
   * @param handle
   * @param [out] available
   * @return
   */
  __declspec(dllexport) int is_super_net_if_available(int handle, int* available);

  /**
   * @brief 设置wifi参数/set wifi parameter
   * @param handle
   * @param param  wifi参数/wifi parameter
   * @return
   */
  __declspec(dllexport) int set_wifi_param(int handle, wifi_param param);

  /**
   * @brief 获取wifi参数/get wifi parameter
   * @param handle
   * @param [out] param WiFi参数/wifi parameter
   *
   * @warning 使用完毕之后请使用free_wifi_param释放param的内存
   * @return
   */
  __declspec(dllexport) int get_wifi_param(int handle, wifi_param* param);

  /**
   * @brief 设置超级网口参数/set super network interface parameter
   * @param handle
   * @param param  超级网口参数/super network interface parameter
   * @return
   */
  __declspec(dllexport) int set_super_net_if_param(int handle, super_net_if_param param);

  /**
   * @brief 获取超级网口参数/get super network interface parameter
   * @param handle
   * @param [out] param 超级网口参数/super network interface parameter
   *
   * @warning 使用完毕之后请使用free_super_net_if_param释放param的内存
   * @return
   */
  __declspec(dllexport) int get_super_net_if_param(int handle, super_net_if_param* param);

  /**
   * @brief 释放内存/free memory
   *
   * @param param
   */
  __declspec(dllexport) void free_super_net_if_param(super_net_if_param* param);

  /**
   * @brief 设置gsm参数/set gsm parameter
   * @param handle
   * @param param  gsm参数/gsm parameter
   * @return
   */
  __declspec(dllexport) int set_4g_param(int handle, four_g_param param);
  /**
   * @brief 获取gsm参数/get gsm parameter
   * @param handle
   * @param [out] param gsm参数/gsm parameter
   *
   * @warning 使用完毕之后请使用free_4g_param释放param的内存
   * @return
   */
  __declspec(dllexport) int get_4g_param(int handle, four_g_param* param);

  /**
   * @brief 释放内存
   *
   * @param param
   */
  __declspec(dllexport) void free_4g_param(four_g_param* param);

  /**
   * @brief 设置USB模式/Set USB mode
   * @param handle 连接句柄/Connection handle
   * @param config USB模式配置/USB mode configuration
   * @return 错误码/Error code
   */
  __declspec(dllexport) int set_usb_mode(int handle, usb_mode_config config);

  /**
   * @brief 获取USB模式/Get USB mode
   * @param handle 连接句柄/Connection handle
   * @param [out] config USB模式配置/USB mode configuration
   * @return 错误码/Error code
   */
  __declspec(dllexport) int get_usb_mode(int handle, usb_mode_config* config);

  /**
   * @brief 导出Nandflash标签数据到文件/Export Nandflash tag data to file
   * @param handle 连接句柄/Connection handle
   * @param year 年份（如25表示2025年）/Year (e.g., 25 for 2025)
   * @param month 月份（1-12）/Month (1-12)
   * @param day 日期（1-31）/Day (1-31)
   * @param output_file_path 输出文件路径/Output file path
   * @return 错误码/Error code
   *
   * @note 该函数会持续读取数据直到完成或发生错误/This function will continuously read data until completion or error
   */
  __declspec(dllexport) int export_nandflash_tag_data(int handle, int year, int month, int day, const char* output_file_path);

  /**
   * @brief 获取核心库版本
   *
   * @return  版本信息
   *
   * @warning 注意使用完返回值之后释放内存
   */
  __declspec(dllexport) char* core_version();

  /**
   * @brief 获取错误描述/get error message description
   *
   * @param code 错误码/error code
   * @return 错误描述信息/error description
   *
   * @warning 注意使用完返回值之后释放内存/free result memory after use
   */
  __declspec(dllexport) void error_description(int code, char* errMessage);

  /**
   * @brief 进入/退出调试模式 enable/disable debug mode
   *
   * @param enable
   */
  __declspec(dllexport) void set_debug(int enable, int toFile);

  /**
   * @brief 启动S108代理服务/start S108 proxy service
   * @return 错误码/error code
   */
  __declspec(dllexport) int start_s108_proxy();

  /**
   * @brief 停止S108代理服务/stop S108 proxy service
   */
  __declspec(dllexport) void stop_s108_proxy();

  /**
   * @brief 连接Silion模块RS232/rs485连接/connect to Silion module via RS232/RS485
   * @param com 串口号/serial port name
   * @param baud 波特率/baud rate
   * @param device_type 设备类型/device type
   * @param timeout 超时时间(秒)/timeout in seconds
   * @param [out] handle 连接句柄/connection handle
   * @return 错误码/error code
   */
  __declspec(dllexport) int connect_silion_rs(char* com, enum baud baud, int device_type, int timeout, int* handle);

  /**
   * @brief connect to silion reader via rs232 with raw int baud rate (bypasses enum baud restriction)
   */
  __declspec(dllexport) int connect_silion_rs_int(char* com, int baud, int device_type, int timeout, int* handle);

  /**
   * @brief 连接M100模块RS232连接/connect to M100 module via RS232
   * @param com 串口号/serial port name
   * @param baud 波特率/baud rate
   * @param timeout 超时时间(秒)/timeout in seconds
   * @param [out] handle 连接句柄/connection handle
   * @return 错误码/error code
   */
  __declspec(dllexport) int connect_m100_rs(char* com, enum baud baud, int timeout, int* handle);

  /**
   * @brief connect to m100 reader via rs232 with raw int baud rate (bypasses enum baud restriction)
   */
  __declspec(dllexport) int connect_m100_rs_int(char* com, int baud, int timeout, int* handle);

  /**
   * @brief connect to r2000 reader via tcp
   * @param ip ip
   * @param port port
   * @param device_type device type
   * @param timeout timeout in seconds
   * @param [out] handle  connection handle
   * @return
   */
  __declspec(dllexport) int connect_r2000_tcp(char* ip, int port, int device_type, int timeout, int* handle);

  /**
   * @brief connect to r2000 reader via udp
   * @param ip
   * @param port
   * @param handle
   * @return
   */
  __declspec(dllexport) int connect_r2000_udp(char* ip, int port, int device_type, int timeout, int* handle);

  /**
   * @brief connect to reader via rs232/rs485
   * @param port serialport
   * @param bd baud rate
   * @param address rs485 address, use 0xff for common use
   * @param device_type device type
   * @param timeout timeout in seconds
   * @param [out] handle
   * @return
   */
  __declspec(dllexport) int connect_r2000_rs(char* port, enum baud bd, int address, int device_type, int timeout, int* handle);

  /**
   * @brief connect to r2000 reader via rs232/rs485 with raw int baud rate
   * @param port serialport
   * @param baud baud rate as raw int (e.g. 460800, 921600), bypasses enum baud restriction
   * @param address rs485 address, use 0xff for common use
   * @param device_type device type
   * @param timeout timeout in seconds
   * @param [out] handle
   * @return
   */
  __declspec(dllexport) int connect_r2000_rs_int(char* port, int baud, int address, int device_type, int timeout, int* handle);

  /**
   * @brief connect to p218 reader via tcp
   * @param ip
   * @param port
   * @param device_type
   * @param timeout
   * @param [out] handle
   * @return
   */
  __declspec(dllexport) int connect_p218_tcp(char* ip, int port, int device_type, int timeout, int* handle);

  /**
   * @brief connect to p218 reader via udp
   * @param ip
   * @param port
   * @param device_type
   * @param timeout
   * @param [out] handle
   * @return
   */
  __declspec(dllexport) int connect_p218_udp(char* ip, int port, int device_type, int timeout, int* handle);

  /**
   * @brief connect_p218_rs
   * @param port
   * @param bd
   * @param address 地址，如果是<=0,表示不带地址参数
   * @param device_type
   * @param handle
   * @param timeout 超时时间，单位秒
   * @return
   */
  __declspec(dllexport) int connect_p218_rs(char* port, enum baud bd, int address, int device_type, int timeout, int* handle);

  /**
   * @brief connect to p218 reader via rs232 with raw int baud rate (bypasses enum baud restriction)
   */
  __declspec(dllexport) int connect_p218_rs_int(char* port, int baud, int address, int device_type, int timeout, int* handle);

  /**
   * @brief USB连接读写器/connect to reader via USB
   * @param di 设备索引/device index
   * @param timeout 超时时间(秒)/timeout in seconds
   * @param [out] handle 连接句柄/connection handle
   * @return 错误码/error code
   */
  __declspec(dllexport) int connect_usb(int di, int timeout, int* handle);

  /**
   * @brief 获取上报选项/get report options
   * @param handle 连接句柄/connection handle
   * @param [out] option_udp UDP上报选项/UDP report option
   * @param [out] option_tcp TCP上报选项/TCP report option
   * @return 错误码/error code
   */
  __declspec(dllexport) int get_report_option(int handle, report_option* option_udp, report_option* option_tcp);

  /**
   * @brief 设置上报选项/set report options
   * @param handle 连接句柄/connection handle
   * @param option_udp UDP上报选项/UDP report option
   * @param option_tcp TCP上报选项/TCP report option
   * @return 错误码/error code
   */
  __declspec(dllexport) int set_report_option(int handle, report_option option_udp, report_option option_tcp);

  // internal api
  /**
   * @brief 从UDP上报选项中获取IP地址(内部API)/get IP address from UDP report option (internal API)
   * @param option 上报选项/report option
   * @return IP地址字符串/IP address string
   */
  __declspec(dllexport) char* internal_get_ip_from_udp_report_option(report_option* option);

  /**
   * @brief 获取HTTP上报URL/get HTTP report URL
   * @param handle 连接句柄/connection handle
   * @param [out] url URL字符串指针/URL string pointer
   * @return 错误码/error code
   * @warning 使用完毕后需要释放内存/free memory after use
   */
  __declspec(dllexport) int get_http_url(int handle, char** url);

  /**
   * @brief 设置HTTP上报URL/set HTTP report URL
   * @param handle 连接句柄/connection handle
   * @param url URL字符串/URL string
   * @return 错误码/error code
   */
  __declspec(dllexport) int set_http_url(int handle, char* url);

  /**
   * @brief 获取简化MQTT配置/get simplified MQTT configuration
   * @param handle 连接句柄/connection handle
   * @param [out] cfg MQTT配置指针/MQTT config pointer
   * @return 错误码/error code
   * @warning 使用完毕后需要使用free_mqtt_simple释放内存/free memory with free_mqtt_simple after use
   */
  __declspec(dllexport) int get_mqtt_simple(int handle, simple_mqtt_config** cfg);

  /**
   * @brief 设置简化MQTT配置/set simplified MQTT configuration
   * @param handle 连接句柄/connection handle
   * @param cfg MQTT配置/MQTT config
   * @return 错误码/error code
   */
  __declspec(dllexport) int set_mqtt_simple(int handle, simple_mqtt_config cfg);

  /**
   * @brief 释放简化MQTT配置内存/free simplified MQTT configuration memory
   * @param cfg MQTT配置指针/MQTT config pointer
   */
  __declspec(dllexport) void free_mqtt_simple(simple_mqtt_config* cfg);

  /**
   * @brief 设置RGB灯模式/set RGB light mode
   * @param handle 连接句柄/connection handle
   * @param mode RGB模式/RGB mode
   * @return 错误码/error code
   */
  __declspec(dllexport) int set_rgb(int handle, int mode);

  /**
   * @brief 设置日志级别/set log level
   * @param level 日志级别/log level (0=panic, 1=fatal, 2=error, 3=warn, 4=info, 5=debug, 6=trace)
   */
  __declspec(dllexport) void set_log_level(int level);

  /**
   * @brief 设置日志输出文件路径/set log output file path
   * @param path 文件路径/file path
   */
  __declspec(dllexport) void set_log_output_file_path(char* path);

  /**
   * @brief 设置日志输出目录/set log output directory
   * @param path 目录路径/directory path
   */
  __declspec(dllexport) void set_log_output_dir(char* path);

  /**
   * @brief 设置是否输出日志到文件/set whether to output log to file
   * @param log_to_file 是否输出到文件/log to file (0 no, 1 yes)
   */
  __declspec(dllexport) void set_log_to_file(int log_to_file);

  /**
   * @brief 设置读写器日志输出到文件/set reader log output to file
   * @param handle 连接句柄/connection handle
   * @param log_to_file 是否输出到文件/log to file (0 no, 1 yes)
   */
  __declspec(dllexport) void set_reader_log_output_file(int handle, int log_to_file);

  /**
   * @brief 设置读写器日志级别/set reader log level
   * @param handle 连接句柄/connection handle
   * @param level 日志级别/log level (0=panic, 1=fatal, 2=error, 3=warn, 4=info, 5=debug, 6=trace)
   */
  __declspec(dllexport) void set_reader_log_level(int handle, int level);

  /**
   * @brief list hf tags
   * @param handle connection handle
   * @param [out] tags
   * @param [out] count number of tags read
   * @return error number
   */
  __declspec(dllexport) int hf_list_tag(int handle, hf_tag* tags, int* count);

  /**
   * @brief read 15693 block data
   * @param handle connection handle
   * @param uid uid of the tag, if not specified, it will choose a random 15693 tag to read
   * @param uid_len uid length
   * @param address where to start read
   * @param block_count number of block to be read
   * @param [out] data data that read
   * @param [out] data_len data length that read
   * @return error code
   */
  __declspec(dllexport) int hf_read_15693_block(
      int handle,
      unsigned char* uid,
      int uid_len,
      int address,
      int block_count,
      unsigned char* data,
      int* data_len);

  /**
   * @brief 读取15693标签块数据(指定块大小)/read 15693 tag block data with block size
   * @param handle 连接句柄/connection handle
   * @param uid 标签UID/tag UID
   * @param uid_len UID长度/UID length
   * @param address 起始地址/start address
   * @param block_count 块数量/block count
   * @param block_size 块大小/block size
   * @param [out] data 读取的数据/data read
   * @param [out] data_len 数据长度/data length
   * @return 错误码/error code
   */
  __declspec(dllexport) int hf_read_15693_block_with_block_size(
      int handle,
      unsigned char* uid,
      int uid_len,
      int address,
      int block_count,
      int block_size,
      unsigned char* data,
      int* data_len);

  /**
   * @brief write block data
   * @param handle connection handle
   * @param uid uid of the tag, if not specified, it will choose a random 15693 tag to read
   * @param uid_len uid length
   * @param address where to start write
   * @param data data that read(only 4 bytes allowed)
   * @param data_len data length that read(fixed 4)
   * @return error code
   */
  __declspec(dllexport) int hf_write_15693_block(int handle, unsigned char* uid, int uid_len, int address, unsigned char* data, int data_len);

  /**
   * @brief 写入15693标签块数据(指定块大小)/write 15693 tag block data with block size
   * @param handle 连接句柄/connection handle
   * @param uid 标签UID/tag UID
   * @param uid_len UID长度/UID length
   * @param address 起始地址/start address
   * @param data 写入数据/data to write
   * @param data_len 数据长度/data length
   * @param block_count 块数量/block count
   * @param block_size 块大小/block size
   * @return 错误码/error code
   */
  __declspec(dllexport) int hf_write_15693_block_with_block_size(
      int handle,
      unsigned char* uid,
      int uid_len,
      int address,
      unsigned char* data,
      int data_len,
      int block_count,
      int block_size);

  /**
   * @brief 写入15693标签多个块数据/write 15693 tag multiple blocks
   * @param handle 连接句柄/connection handle
   * @param uid 标签UID/tag UID
   * @param uid_len UID长度/UID length
   * @param address 起始地址/start address
   * @param data 写入数据/data to write
   * @param data_len 数据长度/data length
   * @return 错误码/error code
   */
  __declspec(dllexport) int hf_write_15693_multi_block(
      int handle,
      unsigned char* uid,
      int uid_len,
      int address,
      unsigned char* data,
      int data_len);

  /**
   * @brief read 14443 block data
   * @param handle connection handle
   * @param uid uid of the tag, if not specified, it will choose a random 14443 tag to read
   * @param uid_len uid length
   * @param address where to start read
   * @param block_count number of block to be read
   * @param [out] data data that read
   * @param [out] data_len data length that read
   * @return error code
   */
  __declspec(dllexport) int hf_read_14443_block(
      int handle,
      unsigned char* uid,
      int uid_len,
      int address,
      int block_count,
      unsigned char* data,
      int* data_len);

  /**
   * @brief write block data
   * @param handle connection handle
   * @param uid uid of the tag, if not specified, it will choose a random 14443 tag to read
   * @param uid_len uid length
   * @param address where to start write
   * @param data data that read(only 4 bytes allowed)
   * @param data_len data length that read(fixed 4)
   * @return error code
   */
  __declspec(dllexport) int hf_write_14443_block(int handle, unsigned char* uid, int uid_len, int address, unsigned char* data, int data_len);

  /**
   * @brief 写入14443标签多个块数据/write 14443 tag multiple blocks
   * @param handle 连接句柄/connection handle
   * @param uid 标签UID/tag UID
   * @param uid_len UID长度/UID length
   * @param address 起始地址/start address
   * @param data 写入数据/data to write
   * @param data_len 数据长度/data length
   * @return 错误码/error code
   */
  __declspec(dllexport) int hf_write_14443_multi_block(
      int handle,
      unsigned char* uid,
      int uid_len,
      int address,
      unsigned char* data,
      int data_len);

  /**
   * @brief select 14443A fan, default 0
   * @param handle connection handle
   * @param uid  uid of the tag, if not specified, it will choose a random 14443 tag to read
   * @param uid_len uid length
   * @param fan fan number
   * @return error code
   */
  __declspec(dllexport) int hf_select_14443_fan(int handle, unsigned char* uid, int uid_len, int fan);

  // ibat2000 connect
  /**
   * @brief IBAT2000 RS232连接/connect to IBAT2000 via RS232
   * @param port 串口号/serial port name
   * @param bd 波特率/baud rate
   * @param [out] handle 连接句柄/connection handle
   * @param timeout 超时时间(秒)/timeout in seconds
   * @return 错误码/error code
   */
  __declspec(dllexport) int ibat2000_rs232_connect(char* port, baud bd, int* handle, int timeout);

  /**
   * @brief connect to IBAT2000 via RS232 with raw int baud rate (bypasses enum baud restriction)
   */
  __declspec(dllexport) int ibat2000_rs232_connect_int(char* port, int baud, int* handle, int timeout);

  // p218 desktop reader keyboard simulator api
  /**
   * @brief 设置键盘模拟输出前缀/set keyboard simulator output prefix
   * @param handler 连接句柄/connection handle
   * @param enable 是否启用/enable (0 no, 1 yes)
   * @param prefix 前缀字符串/prefix string
   * @return 错误码/error code
   */
  __declspec(dllexport) int set_sk_output_prefix(int handler, int enable, char* prefix);

  /**
   * @brief 获取键盘模拟输出前缀/get keyboard simulator output prefix
   * @param handler 连接句柄/connection handle
   * @param [out] enable 是否启用/enable (0 no, 1 yes)
   * @param [out] prefix 前缀字符串/prefix string
   * @return 错误码/error code
   */
  __declspec(dllexport) int get_sk_output_prefix(int handler, int* enable, char* prefix);

  /**
   * @brief 设置键盘模拟输出格式/set keyboard simulator output format
   * @param handler 连接句柄/connection handle
   * @param format 输出格式/output format
   * @return 错误码/error code
   */
  __declspec(dllexport) int set_sk_output_format(int handler, int format);

  /**
   * @brief 获取键盘模拟输出格式/get keyboard simulator output format
   * @param handler 连接句柄/connection handle
   * @param [out] format 输出格式/output format
   * @return 错误码/error code
   */
  __declspec(dllexport) int get_sk_output_format(int handler, int* format);

  /**
   * @brief 设置键盘模拟输出地址/set keyboard simulator output address
   * @param handler 连接句柄/connection handle
   * @param addr 地址/address
   * @param len 长度/length
   * @return 错误码/error code
   */
  __declspec(dllexport) int set_sk_output_addr(int handler, int addr, int len);

  /**
   * @brief 获取键盘模拟输出地址/get keyboard simulator output address
   * @param handler 连接句柄/connection handle
   * @param [out] addr 地址/address
   * @param [out] len 长度/length
   * @return 错误码/error code
   */
  __declspec(dllexport) int get_sk_output_addr(int handler, int* addr, int* len);

  /**
   * @brief 设置键盘模拟输出间隔/set keyboard simulator output interval
   * @param handler 连接句柄/connection handle
   * @param interval 间隔时间(毫秒)/interval in milliseconds
   * @return 错误码/error code
   */
  __declspec(dllexport) int set_sk_output_interval(int handler, int interval);

  /**
   * @brief 获取键盘模拟输出间隔/get keyboard simulator output interval
   * @param handler 连接句柄/connection handle
   * @param [out] interval 间隔时间(毫秒)/interval in milliseconds
   * @return 错误码/error code
   */
  __declspec(dllexport) int get_sk_output_interval(int handler, int* interval);

  /**
   * @brief 设置键盘模拟换行回车/set keyboard simulator new line enter
   * @param handler 连接句柄/connection handle
   * @param enable 是否启用/enable (0 no, 1 yes)
   * @return 错误码/error code
   */
  __declspec(dllexport) int set_sk_new_line_enter(int handler, int enable);

  /**
   * @brief 获取键盘模拟换行回车/get keyboard simulator new line enter
   * @param handler 连接句柄/connection handle
   * @param [out] enable 是否启用/enable (0 no, 1 yes)
   * @return 错误码/error code
   */
  __declspec(dllexport) int get_sk_new_line_enter(int handler, int* enable);

  /**
   * @brief 获取键盘模拟输出内存区/get keyboard simulator output memory bank
   * @param handler 连接句柄/connection handle
   * @param [out] bank 内存区/memory bank
   * @return 错误码/error code
   */
  __declspec(dllexport) int get_sk_output_memory_bank(int handler, int* bank);

  /**
   * @brief 设置键盘模拟输出内存区/set keyboard simulator output memory bank
   * @param handler 连接句柄/connection handle
   * @param bank 内存区/memory bank
   * @return 错误码/error code
   */
  __declspec(dllexport) int set_sk_output_memory_bank(int handler, int bank);

  // eas api
  /**
   * @brief 修改EAS状态/change EAS state
   * @param handler 连接句柄/connection handle
   * @param epc 标签EPC/tag EPC
   * @param epc_len EPC长度/EPC length
   * @param password 访问密码/access password
   * @param password_len 密码长度/password length
   * @param status EAS状态/EAS status
   * @return 错误码/error code
   */
  __declspec(dllexport) int change_eas_state(int handler, unsigned char* epc, int epc_len, unsigned char* password, int password_len, int status);

  /**
   * @brief EAS报警/EAS alarm
   * @param handler 连接句柄/connection handle
   * @return 错误码/error code
   */
  __declspec(dllexport) int eas_alarm(int handler);

  // p218 start auto mode tmp
  /**
   * @brief P218启动临时自动模式/P218 start temporary auto mode
   * @param handler 连接句柄/connection handle
   * @return 错误码/error code
   */
  __declspec(dllexport) int start_auto_mode_tmp(int handler);

  // p218 stop auto mode tmp
  /**
   * @brief P218停止临时自动模式/P218 stop temporary auto mode
   * @param handler 连接句柄/connection handle
   * @return 错误码/error code
   */
  __declspec(dllexport) int stop_auto_mode_tmp(int handler);

  typedef void (*on_client_reader_connected)(int);
  /**
   * @brief listen vup tcp client mode reader
   * @param port port to be listened
   * @param callback callback called when a reader connected
   * @return error code
   */
  __declspec(dllexport) int listen_vup_tcp_client_mode_reader(int port, on_client_reader_connected callback);

  // stop listen vup tcp client mode reader
  /**
   * @brief 停止监听VUP TCP客户端模式读写器/stop listening VUP TCP client mode reader
   * @param port 监听端口/listen port
   */
  __declspec(dllexport) void stop_listen_vup_tcp_client_mode_reader(int port);

  // led tag
  /**
   * @brief LED标签闪烁/LED tag flicker
   * @param handler 连接句柄/connection handle
   * @param epc 标签EPC/tag EPC
   * @param epc_len EPC长度/EPC length
   */
  __declspec(dllexport) void led_tag_flicker(int handler, unsigned char* epc, int epc_len);

  /**
   * @brief LED标签长闪烁/LED tag long flicker
   * @param handler 连接句柄/connection handle
   * @param epc 标签EPC/tag EPC
   * @param epc_len EPC长度/EPC length
   */
  __declspec(dllexport) void led_tag_long_flicker(int handler, unsigned char* epc, int epc_len);

  // r2000 johar temperature
  /**
   * @brief 启用Johar温度标签读取功能/enable Johar temperature tag reading function
   * @param handler 连接句柄/connection handle
   * @param enable 是否启用温度读取/enable temperature reading (0 no, 1 yes)
   * @param is_temp 是否作为温度标签处理/process as temperature tag (0 no, 1 yes)
   * @return 错误码/error code
   */
  __declspec(dllexport) int enable_temperature(int handler, int enable, int is_temp);

  // r2000 fast ants switch inventory command
  /**
   * @brief R2000快速天线切换盘点/R2000 fast antenna switch inventory
   * @param handler 连接句柄/connection handle
   * @param ants 天线数组/antenna array
   * @param ants_count 天线数量/antenna count
   * @param [out] output 输出数据指针/output data pointer
   * @param [out] count 输出数量/output count
   * @return 错误码/error code
   * @warning 使用完毕后需要释放内存/free memory after use
   */
  __declspec(dllexport) int r2000_fast_ant_switch_inventory(int handler, int* ants, int ants_count, auto_read_output** output, int* count);

  /**
   * @brief R2000快速天线切换盘点(扩展版)/R2000 fast antenna switch inventory (extended version)
   * @param handler 连接句柄/connection handle
   * @param ants 天线数组/antenna array
   * @param antCount 天线数量/antenna count
   * @param stays 停留时间数组/stay time array
   * @param stayCount 停留时间数量/stay time count
   * @param interval 间隔时间/interval
   * @param repeat 重复次数/repeat count
   * @param session session参数/session parameter
   * @param target target参数/target parameter
   * @param phase phase参数/phase parameter
   * @param [out] output 输出数据指针/output data pointer
   * @param [out] count 输出数量/output count
   * @return 错误码/error code
   * @warning 使用完毕后需要释放内存/free memory after use
   */
  __declspec(dllexport) int r2000_fast_ant_switch_inventory2(
      int handler,
      int* ants,
      int antCount,
      int* stays,
      int stayCount,
      int interval,
      int repeat,
      int session,
      int target,
      int phase,
      auto_read_output** output,
      int* count);

  /**
   * @brief R2000读取多个内存区/R2000 read multiple memory banks
   * @param handler 连接句柄/connection handle
   * @param epc 标签EPC/tag EPC
   * @param epc_len EPC长度/EPC length
   * @param password_addr 密码区地址/password bank address
   * @param password_len 密码区长度/password bank length
   * @param tid_addr TID区地址/TID bank address
   * @param tid_len TID区长度/TID bank length
   * @param user_addr USER区地址/USER bank address
   * @param user_len USER区长度/USER bank length
   * @param session session参数/session parameter
   * @param target target参数/target parameter
   * @param read_mode 读取模式/read mode
   * @param timeout 超时时间(毫秒)/timeout in milliseconds
   * @param password 访问密码/access password
   * @param pswd_len 密码长度/password length
   * @param [out] cTag 标签数据指针/tag data pointer
   * @param [out] cTagCount 标签数量/tag count
   * @return 错误码/error code
   * @warning 使用完毕后需要释放标签内存/free tags memory after use
   */
  __declspec(dllexport) int r2000_read_multiple_memory(
      int handler,
      unsigned char* epc,
      int epc_len,
      int password_addr,
      int password_len,
      int tid_addr,
      int tid_len,
      int user_addr,
      int user_len,
      int session,
      int target,
      int read_mode,
      int timeout,
      unsigned char* password,
      int pswd_len,
      tag** cTag,
      int* cTagCount);

  /**
   * @brief 获取读写器温度/get reader temperature
   * @param handler 连接句柄/connection handle
   * @param [out] temperature 温度值(摄氏度)/temperature value in Celsius
   * @return 错误码/error code
   */
  __declspec(dllexport) int get_reader_temperature(int handler, int* temperature);

  /**
   * @brief R2000自定义Session盘点/R2000 custom session inventory
   * @param handler 连接句柄/connection handle
   * @param session session参数/session parameter (0-3)
   * @param target target参数/target parameter (0-1)
   * @param repeat 重复次数/repeat count
   * @param sl SL参数/SL parameter
   * @param phase phase参数/phase parameter
   * @param powerSave 省电模式/power save mode
   * @param [out] output 输出数据指针/output data pointer
   * @param [out] count 输出数量/output count
   * @return 错误码/error code
   * @warning 使用完毕后需要释放内存/free memory after use
   */
  __declspec(dllexport) int r2000_custom_session_inventory(
      int handler,
      int session,
      int target,
      int repeat,
      int sl,
      int phase,
      int powerSave,
      auto_read_output** output,
      int* count);

  /**
   * @brief r2000_set_epc_match
   * @param handle
   * @param epc
   * @param epc_len clear match if the epc_len == 0
   * @return
   */
  __declspec(dllexport) int r2000_set_epc_match(int handle, unsigned char* epc, int epc_len);

  /**
   * @brief r2000_get_epc_match
   * @param handle
   * @param epc
   * @param epc_len when epc_len ==0, it means there is no match
   * @return
   */
  __declspec(dllexport) int r2000_get_epc_match(int handle, unsigned char** epc, int* epc_len);

  /**
   * @brief 获取射频链路配置文件/get RF link profile
   * @param handle 连接句柄/connection handle
   * @param [out] profile 配置文件索引/profile index
   * @return 错误码/error code
   */
  __declspec(dllexport) int get_rf_link_profile(int handle, int* profile);

  /**
   * @brief 设置射频链路配置文件/set RF link profile
   * @param handle 连接句柄/connection handle
   * @param profile 配置文件索引/profile index
   * @return 错误码/error code
   */
  __declspec(dllexport) int set_rf_link_profile(int handle, int profile);

  /**
   * @brief 获取设备类型代码/get device type code
   * @param handle 连接句柄/connection handle
   * @param [out] code 设备类型代码/device type code
   * @return 错误码/error code
   */
  __declspec(dllexport) int get_dev_type_code(int handle, int* code);

  /**
   * @brief 设置设备类型代码/set device type code
   * @param handle 连接句柄/connection handle
   * @param code 设备类型代码/device type code
   * @return 错误码/error code
   */
  __declspec(dllexport) int set_dev_type_code(int handle, int code);

  /**
   * @brief 添加心跳通知回调/add heartbeat notify callback
   * @param handle 连接句柄/connection handle
   * @param cb 回调函数/callback function
   * @return 错误码/error code
   */
  __declspec(dllexport) int add_heartbeat_notify_callback(int handle, notify_callback_heartbeat cb);

  /**
   * @brief 移除心跳通知回调/remove heartbeat notify callback
   * @param handle 连接句柄/connection handle
   * @return 错误码/error code
   */
  __declspec(dllexport) int remove_heartbeat_notify_callback(int handle);

  /**
   * @brief 添加GPO通知回调/add GPO notify callback
   * @param handle 连接句柄/connection handle
   * @param cb 回调函数/callback function
   * @return 错误码/error code
   */
  __declspec(dllexport) int add_gpo_notify_callback(int handle, notify_callback_gpo cb);

  /**
   * @brief 移除GPO通知回调/remove GPO notify callback
   * @param handle 连接句柄/connection handle
   * @return 错误码/error code
   */
  __declspec(dllexport) int remove_gpo_notify_callback(int handle);

  /**
   * @brief 获取Modbus端口/get Modbus port
   * @param handle 连接句柄/connection handle
   * @param [out] port 端口号/port number
   * @return 错误码/error code
   */
  __declspec(dllexport) int get_modbus_port(int handle, int* port);

  // x rfid module
  /**
   * @brief X系列单次盘点/X series single inventory
   * @param handle 连接句柄/connection handle
   * @param timeout 超时时间(毫秒)/timeout in milliseconds
   * @param option 选项/option
   * @param meta_flags 元数据标志/meta flags
   * @param select_address 选择地址/select address
   * @param select_data_len 选择数据长度/select data length
   * @param select_data 选择数据/select data
   * @param data_len 数据长度/data length
   * @param password 访问密码/access password
   * @param password_len 密码长度/password length
   * @param [out] tag 标签数据/tag data
   * @return 错误码/error code
   */
  __declspec(dllexport) int x_single_inventory(
      int handle,
      unsigned short timeout,
      unsigned char option,
      unsigned short meta_flags,
      unsigned int select_address,
      short select_data_len,
      unsigned char* select_data,
      int data_len,
      unsigned char* password,
      int password_len,
      tag* tag);

  /**
   * @brief x_sync_inventory 同步盘存
   * @param handle
   * @param timeout
   * @param option
   * @param meta_flags
   * @param select_address
   * @param select_data_len
   * @param select_data
   * @param data_len
   * @param search_flags
   * @param password
   * @param password_len
   * @param tag 本次盘存返回的标签
   * @param tag_number 本次盘存返回的标签数量
   * @return
   */
  __declspec(dllexport) int x_sync_inventory(
      int handle,
      unsigned short timeout,
      unsigned char option,
      unsigned short meta_flags,
      unsigned int select_address,
      short select_data_len,
      unsigned char* select_data,
      int data_len,
      short search_flags,
      unsigned char* password,
      int password_len,
      tag** tag,
      int* tag_number);

  /**
   * @brief X系列异步盘点/X series asynchronous inventory
   * @param handle 连接句柄/connection handle
   * @param option 选项/option
   * @param meta_flags 元数据标志/meta flags
   * @param select_address 选择地址/select address
   * @param select_data_len 选择数据长度/select data length
   * @param select_data 选择数据/select data
   * @param data_len 数据长度/data length
   * @param search_flags 搜索标志/search flags
   * @param password 访问密码/access password
   * @param password_len 密码长度/password length
   * @return 错误码/error code
   */
  __declspec(dllexport) int x_async_inventory(
      int handle,
      unsigned char option,
      unsigned short meta_flags,
      unsigned int select_address,
      short select_data_len,
      unsigned char* select_data,
      int data_len,
      short search_flags,
      unsigned char* password,
      int password_len);

  /**
   * @brief 停止X系列同步盘点/stop X series synchronous inventory
   * @param handle 连接句柄/connection handle
   * @return 错误码/error code
   */
  __declspec(dllexport) int x_stop_sync_inventory(int handle);

  /**
   * @brief x_ex_async_inventory
   * @param handle
   * @param option
   * @param meta_flags
   * @param select_address
   * @param select_data_len
   * @param select_data
   * @param data_len
   * @param search_flags
   * @param ex_data 20 bytes ExConfigData
   * @param ex_len
   * @param password
   * @param password_len
   * @return
   */
  __declspec(dllexport) int x_ex_async_inventory(
      int handle,
      unsigned char option,
      unsigned short meta_flags,
      unsigned int select_address,
      short select_data_len,
      unsigned char* select_data,
      int data_len,
      short search_flags,
      unsigned char* ex_data,
      int ex_len,
      unsigned char* password,
      int password_len);

  /**
   * @brief 停止X系列扩展同步盘点/stop X series extended synchronous inventory
   * @param handle 连接句柄/connection handle
   * @return 错误码/error code
   */
  __declspec(dllexport) int x_stop_ex_sync_inventory(int handle);

  // m100

  /**
   * @brief Q系列插入频点/Q series insert frequency points
   * @param handle 连接句柄/connection handle
   * @param auto_jump 是否自动跳频/auto jump frequency
   * @param points 频点数组/frequency points array
   * @param count 频点数量/frequency points count
   * @return 错误码/error code
   */
  __declspec(dllexport) int q_insert_freq_points(int handle, int auto_jump, int* points, int count);

  /**
   * @brief Q系列保存参数/Q series save parameters
   * @param handle 连接句柄/connection handle
   * @param save 是否保存/save (0 no, 1 yes)
   * @return 错误码/error code
   */
  __declspec(dllexport) int q_save_param(int handle, int save);

  /**
   * @brief Q系列获取固定频率索引/Q series get fixed frequency index
   * @param handle 连接句柄/connection handle
   * @param [out] freq 频率索引/frequency index
   * @return 错误码/error code
   */
  __declspec(dllexport) int q_get_fix_freq_index(int handle, int* freq);

  /**
   * @brief Q系列设置固定频率索引/Q series set fixed frequency index
   * @param handle 连接句柄/connection handle
   * @param freq 频率索引/frequency index
   * @return 错误码/error code
   */
  __declspec(dllexport) int q_set_fix_freq_index(int handle, int freq);

  /**
   * @brief Q系列多次盘点/Q series multiple inventory
   * @param handle 连接句柄/connection handle
   * @param times 盘点次数/inventory times
   * @return 错误码/error code
   */
  __declspec(dllexport) int q_multi_inventory(int handle, int times);

  /**
   * @brief Q系列停止多次盘点/Q series stop multiple inventory
   * @param handle 连接句柄/connection handle
   * @return 错误码/error code
   */
  __declspec(dllexport) int q_stop_multi_inventory(int handle);

  /**
   * @brief X系列设置自动跳频频点/X series set auto jump frequency points
   * @param handle 连接句柄/connection handle
   * @param points 频点数组/frequency points array
   * @param pointCount 频点数量/frequency points count
   * @return 错误码/error code
   */
  extern __declspec(dllexport) int x_set_auto_jump_freq_points(int handle, int* points, int pointCount);

  /**
   * @brief X系列获取自动跳频频点/X series get auto jump frequency points
   * @param handle 连接句柄/connection handle
   * @param [out] points 频点数组指针/frequency points array pointer
   * @param [out] pointCount 频点数量/frequency points count
   * @return 错误码/error code
   * @warning 使用完毕后需要释放内存/free memory after use
   */
  extern __declspec(dllexport) int x_get_auto_jump_freq_points(int handle, int** points, int* pointCount);

  /**
   * @brief X系列测试回波损耗/X series test return loss
   * @param handle 连接句柄/connection handle
   * @param power 功率/power
   * @param ant 天线号/antenna number
   * @param region 频段/region
   * @param numberOfFreq 频点数量/number of frequencies
   * @param [out] rl 回波损耗数组指针/return loss array pointer
   * @param [out] rlCount 回波损耗数量/return loss count
   * @return 错误码/error code
   * @warning 使用完毕后需要释放内存/free memory after use
   */
  extern __declspec(dllexport) int x_test_return_loss(int handle, int power, int ant, freq_region region, int numberOfFreq, int** rl, int* rlCount);

  /**
   * @brief 获取自动重启选项/get auto reboot option
   * @param handle 连接句柄/connection handle
   * @param [out] option 自动重启选项/auto reboot option
   * @return 错误码/error code
   */
  extern __declspec(dllexport) int get_auto_reboot_option(int handle, auto_reboot_option* option);

  /**
   * @brief 设置自动重启选项/set auto reboot option
   * @param handle 连接句柄/connection handle
   * @param option 自动重启选项/auto reboot option
   * @return 错误码/error code
   */
  extern __declspec(dllexport) int set_auto_reboot_option(int handle, auto_reboot_option option);

  // fm

  /// @brief 连接fm模块
  /// @param port 串口号
  /// @param baud  波特率
  /// @param address  地址，0-255，超过范围表示不指定地址
  /// @param timeout  超时时间
  /// @param di
  /// @param handle  连接句柄
  /// @return  错误码
  __declspec(dllexport) int fm_connect_rs(char* port, int baud, int address, int timeout, int di, int* handle);

  /// @brief 读寄存器
  /// @param handle 连接句柄
  /// @param reg
  /// 0x00：RFID（RFID芯片寄存器，下电失效）
  /// 0x01：HOST（HOST系统寄存器，下电失效）
  /// 0x02：OEM（OEM配置寄存器，下电保存）
  /// @param address 寄存器地址
  /// @param value 寄存器值
  /// @return 错误码
  __declspec(dllexport) int fm_reg_read(int handle, unsigned char reg, int address, int* value);

  /// @brief  写寄存器
  /// @param handle 连接句柄
  /// @param reg 寄存器类型
  /// 0x00：RFID（RFID芯片寄存器，下电失效）
  /// 0x01：HOST（HOST系统寄存器，下电失效）
  /// 0x02：OEM（OEM配置寄存器，下电保存）
  /// @param address 寄存器地址
  /// @param value 寄存器值
  /// @return 错误码
  __declspec(dllexport) int fm_reg_write(int handle, unsigned char reg, int address, int value);

  /// @brief 设置定频
  /// @param handle  连接句柄
  /// @param region  频段
  /// @param freq   频率
  /// @param isFix  是否固定频率
  /// @return  错误码
  __declspec(dllexport) int fm_set_fix_freq(int handle, freq_region region, int freq, int isFix);

  /**
   * @brief FM模块设置跳频/FM module set hop frequency
   * @param handle 连接句柄/connection handle
   * @param region 频段/region
   * @param freqPoints 频点数组/frequency points array
   * @param count 频点数量/frequency points count
   * @param auto_hop 是否自动跳频/auto hop
   * @return 错误码/error code
   */
  __declspec(dllexport) int fm_set_hop_freq(int handle, freq_region region, unsigned char* freqPoints, int count, int auto_hop);

  /**
   * @brief FM模块获取跳频/FM module get hop frequency
   * @param handle 连接句柄/connection handle
   * @param [out] region 频段/region
   * @param [out] freqPoints 频点数组/frequency points array
   * @param [out] count 频点数量/frequency points count
   * @param [out] auto_hop 是否自动跳频/auto hop
   * @return 错误码/error code
   */
  __declspec(dllexport) int fm_get_hop_freq(int handle, freq_region* region, unsigned char* freqPoints, int* count, int* auto_hop);

  /**
   * @brief FM天线参数结构体/FM antenna parameter structure
   */
  typedef struct fm_ant_param
  {
    int save_mode;       // -1 ignore
    int heartbeat;       // -1 ignore
    int profile_num;     // -1 ignore
    int target;          // -1 ignore
    int session;         // -1 ignore
    int tag_focus;       // -1 ignore
    int ant_channel[16]; // ant bitmap -1 ignore
    int powers0[16];
    int powers1[16];
    int query_time[16];
    int query_count[16];
    int query_circle[16];
    int ant_interval;        // -1 ignore
    int ant_circle_interval; // -1 ignore
    int ant_circle_count;    // -1 ignore
  } fm_ant_param;

  /**
   * @brief FM模块获取天线参数/FM module get antenna parameters
   * @param handle 连接句柄/connection handle
   * @param [out] param 天线参数/antenna parameters
   * @return 错误码/error code
   */
  __declspec(dllexport) int fm_get_ant_param(int handle, fm_ant_param* param);

  /**
   * @brief FM模块设置天线参数/FM module set antenna parameters
   * @param handle 连接句柄/connection handle
   * @param param 天线参数/antenna parameters
   * @return 错误码/error code
   */
  __declspec(dllexport) int fm_set_ant_param(int handle, fm_ant_param param);

  /// 钩子断开连接回调/hook disconnect callback
  typedef int (*hook_disconnect_fn)();
  /// 钩子连接回调/hook connect callback
  typedef int (*hook_connect_fn)();
  /// 钩子发送回调/hook send callback
  typedef int (*hook_send_fn)(unsigned char*, int);
  /// 钩子接收回调/hook receive callback
  typedef int (*hook_recv_fn)(unsigned char*);

  /**
   * @brief 连接Hook读写器/connect to hook reader
   * @param send_fn 发送回调函数/send callback function
   * @param receive_fn 接收回调函数/receive callback function
   * @param connect_fn 连接回调函数/connect callback function
   * @param disconnect_fn 断开连接回调函数/disconnect callback function
   * @param address 地址/address
   * @param timeout 超时时间(秒)/timeout in seconds
   * @param [out] handler 连接句柄/connection handle
   * @return 错误码/error code
   */
  __declspec(dllexport) int connect_hook_reader(hook_send_fn send_fn, hook_recv_fn receive_fn, hook_connect_fn connect_fn, hook_disconnect_fn disconnect_fn, int address, int timeout, int* handler);

  // VUP 协议子命令接口 / VUP Protocol Sub-command Interface

  /**
   * @brief 标签信息文件结构体前向声明 / Forward declaration of tagInFile structure
   *
   * 完整定义在 vh88.h 中 / Full definition in vh88.h
   */
  struct tagInFile;

  /**
   * @brief 配置工作模块 / Configure work module
   * @param handle 连接句柄 / Connection handle
   * @param device_type 设备种类 / Device type
   *        0x00 = RFID设备 / RFID device
   *        0x01 = N4313扫描头 / N4313 scanner
   * @param work_mode 工作模式 / Work mode
   *        0x00 = R2000美频 / R2000 US frequency
   *        0x01 = R2000欧频 / R2000 EU frequency
   *        0x02 = 5F美频 / 5F US frequency
   *        0x03 = 5F欧频 / 5F EU frequency
   *        0x04 = 5S美频 / 5S US frequency
   *        0x05 = 5S欧频 / 5S EU frequency
   * @return 错误码，0表示成功 / Error code, 0 for success
   */
  __declspec(dllexport) int rfid_config_work_module(int handle, unsigned char device_type, unsigned char work_mode);

  /**
   * @brief 配置设备语言 / Configure device language
   * @param handle 连接句柄 / Connection handle
   * @param language 语言 / Language
   *        0x00 = 中文 / Chinese
   *        0x01 = 英文 / English
   * @return 错误码，0表示成功 / Error code, 0 for success
   */
  __declspec(dllexport) int rfid_config_language(int handle, unsigned char language);

  /**
   * @brief 配置RFID盘点参数 / Configure RFID inventory parameters
   * @param handle 连接句柄 / Connection handle
   * @param interval 盘存间隔，单位ms / Inventory interval in milliseconds
   * @param session Session参数 / Session parameter
   *        0x00 = Session 0
   *        0x01 = Session 1
   * @return 错误码，0表示成功 / Error code, 0 for success
   */
  __declspec(dllexport) int rfid_config_inventory_params(int handle, unsigned char interval, unsigned char session);

  /**
   * @brief 配置蓝牙名称 / Configure Bluetooth name
   * @param handle 连接句柄 / Connection handle
   * @param name 蓝牙名称字符串，最大8字节 / Bluetooth name string, max 8 bytes
   * @return 错误码，0表示成功 / Error code, 0 for success
   */
  __declspec(dllexport) int rfid_config_bluetooth_name(int handle, const char* name);

  /**
   * @brief 获取RFID盘点参数 / Get RFID inventory parameters
   * @param handle 连接句柄 / Connection handle
   * @param interval 输出参数：盘存间隔，单位ms / Output: Inventory interval in milliseconds
   * @param session 输出参数：Session参数 / Output: Session parameter
   * @return 错误码，0表示成功 / Error code, 0 for success
   */
  __declspec(dllexport) int rfid_get_inventory_params(int handle, unsigned char* interval, unsigned char* session);

  /**
   * @brief 获取蓝牙名称 / Get Bluetooth name
   * @param handle 连接句柄 / Connection handle
   * @param name 输出参数：蓝牙名称字符串指针，调用者需要使用free()释放 / Output: Bluetooth name string pointer, caller must free()
   * @return 错误码，0表示成功 / Error code, 0 for success
   */
  __declspec(dllexport) int rfid_get_bluetooth_name(int handle, char** name);

  /**
   * @brief 配置待机时间 / Configure standby time
   * @param handle 连接句柄 / Connection handle
   * @param standby_time 待机时间，单位分钟，范围1-43200 / Standby time in minutes, range 1-43200
   * @return 错误码，0表示成功 / Error code, 0 for success
   */
  __declspec(dllexport) int rfid_config_standby_time(int handle, int standby_time);

  /**
   * @brief 配置过滤重复标签 / Configure filter duplicate tags
   * @param handle 连接句柄 / Connection handle
   * @param enable 0-关闭，1-打开 / 0-disable, 1-enable
   * @return 错误码，0表示成功 / Error code, 0 for success
   */
  __declspec(dllexport) int rfid_config_filter_duplicate(int handle, unsigned char enable);

  /**
   * @brief 配置SECS I通信参数 / Configure SECS I communication parameters
   * @param handle 连接句柄 / Connection handle
   * @param config_item 配置项：0x00=设备地址, 0x01=T1, 0x02=T2, 0x03=T3, 0x04=T4, 0x05=重试次数
   * @param value 配置值 / Configuration value
   * @return 错误码，0表示成功 / Error code, 0 for success
   */
  __declspec(dllexport) int rfid_config_secs_i(int handle, unsigned char config_item, int value);

  /**
   * @brief 获取设备工作模块 / Get device work module
   * @param handle 连接句柄 / Connection handle
   * @param device_type 输出参数：设备类型 / Output: Device type
   *        0x00 = RFID
   *        0x01 = N4313扫描头 / N4313 scanner
   * @param work_mode 输出参数：工作模式 / Output: Work mode
   *        0x00 = R2000美频 / R2000 US
   *        0x01 = R2000欧频 / R2000 EU
   *        0x02 = 5F美频 / 5F US
   *        0x03 = 5F欧频 / 5F EU
   *        0x04 = 5S美频 / 5S US
   *        0x05 = 5S欧频 / 5S EU
   * @return 错误码，0表示成功 / Error code, 0 for success
   */
  __declspec(dllexport) int rfid_get_work_module(int handle, unsigned char* device_type, unsigned char* work_mode);

  /**
   * @brief 获取设备语言 / Get device language
   * @param handle 连接句柄 / Connection handle
   * @param language 输出参数：语言代码 / Output: Language code
   *        0x00 = 中文 / Chinese
   *        0x01 = 英文 / English
   * @return 错误码，0表示成功 / Error code, 0 for success
   */
  __declspec(dllexport) int rfid_get_language(int handle, unsigned char* language);

  /**
   * @brief 获取待机时间 / Get standby time
   * @param handle 连接句柄 / Connection handle
   * @param standby_time 输出参数：待机时间（2字节，高位在前低位在后，单位分钟） / Output: Standby time (2 bytes, big-endian, in minutes)
   * @return 错误码，0表示成功 / Error code, 0 for success
   */
  __declspec(dllexport) int rfid_get_standby_time(int handle, unsigned char* standby_time);

  /**
   * @brief 获取过滤重复标签状态 / Get filter duplicate tags status
   * @param handle 连接句柄 / Connection handle
   * @param enable 输出参数：0-关闭，1-打开 / Output: 0-disable, 1-enable
   * @return 错误码，0表示成功 / Error code, 0 for success
   */
  __declspec(dllexport) int rfid_get_filter_duplicate(int handle, unsigned char* enable);

  /**
   * @brief 获取SECS I通信参数 / Get SECS I communication parameters
   * @param handle 连接句柄 / Connection handle
   * @param config_item 配置项：0x00=设备地址, 0x01=T1, 0x02=T2, 0x03=T3, 0x04=T4, 0x05=重试次数
   * @param value 输出参数：配置项对应值（2字节，高位在前低位在后） / Output: value (2 bytes, big-endian)
   * @return 错误码，0表示成功 / Error code, 0 for success
   */
  __declspec(dllexport) int rfid_get_secs_i(int handle, unsigned char config_item, unsigned char* value);

  /**
   * @brief 获取设备电量 / Get device battery level
   * @param handle 连接句柄 / Connection handle
   * @param battery 输出参数：电量百分比 (0-100) / Output: Battery percentage (0-100)
   * @return 错误码，0表示成功 / Error code, 0 for success
   */
  __declspec(dllexport) int rfid_get_battery(int handle, unsigned char* battery);

  /**
   * @brief 获取设备文件目录 / Get device file directory
   * @param handle 连接句柄 / Connection handle
   * @param directories 输出参数：目录字符串数组指针，调用者需要使用rfid_free_string_array()释放 / Output: Directory string array pointer, caller must use rfid_free_string_array() to free
   * @param count 输出参数：目录数量 / Output: Directory count
   * @return 错误码，0表示成功 / Error code, 0 for success
   */
  __declspec(dllexport) int rfid_get_file_directory(int handle, char*** directories, int* count);

  /**
   * @brief 释放字符串数组 / Free string array
   * @param arr 字符串数组指针 / String array pointer
   * @param size 数组大小 / Array size
   */
  __declspec(dllexport) void rfid_free_string_array(char** arr, int size);

  /**
   * @brief 下载盘存数据 / Download inventory data
   * @param handle 连接句柄 / Connection handle
   * @param directory 目录字符串 / Directory string
   * @param files 输出参数：标签信息文件数组指针，调用者需要使用rfid_free_tag_info_files()释放 / Output: Tag info file array pointer, caller must use rfid_free_tag_info_files() to free
   * @param count 输出参数：文件数量 / Output: File count
   * @return 错误码，0表示成功 / Error code, 0 for success
   */
  __declspec(dllexport) int rfid_download_inventory_data(int handle, const char* directory, struct tagInFile** files, int* count);

  /**
   * @brief 释放标签信息文件数组 / Free tag info file array
   * @param files 标签信息文件数组指针 / Tag info file array pointer
   * @param count 文件数量 / File count
   */
  __declspec(dllexport) void rfid_free_tag_info_files(struct tagInFile* files, int count);

  /**
   * @brief 删除盘存数据 / Delete inventory data
   * @param handle 连接句柄 / Connection handle
   * @param directory 目录字符串 / Directory string
   * @return 错误码，0表示成功 / Error code, 0 for success
   */
  __declspec(dllexport) int rfid_delete_inventory_data(int handle, const char* directory);

  /**
   * @brief 保存盘存数据 / Save inventory data
   * @param handle 连接句柄 / Connection handle
   * @return 错误码，0表示成功 / Error code, 0 for success
   */
  __declspec(dllexport) int rfid_save_inventory_data(int handle);

#ifdef __cplusplus
}
#endif

#endif

/**
 * @brief 读取低频标签数据
 * @param handle 连接句柄
 * @param ant 天线号 (0x01~0x04)
 * @param start_block 起始块号 (1~15)，1开始编号
 * @param read_len 读取块数 (1~15)，每块8字节
 * @param output_data 输出数据缓冲区
 * @param output_len 输出数据长度
 * @return 错误码
 */
__declspec(dllexport) int read_low_freq_tag(int handle, unsigned char ant, unsigned char start_block, unsigned char read_len, unsigned char* output_data, int* output_len);

/**
 * @brief 写入低频标签数据
 * @param handle 连接句柄
 * @param ant 天线号 (0x01~0x04)
 * @param start_block 写入块号 (1~15)，1开始编号
 * @param write_data 写入数据，长度必须是8的倍数
 * @param write_len 写入数据长度，必须是8的倍数
 * @return 错误码
 */
__declspec(dllexport) int write_low_freq_tag(int handle, unsigned char ant, unsigned char start_block, const unsigned char* write_data, int write_len);

/**
 * @brief 写入低频标签（可自定义超时时间）
 * @param handle 连接句柄
 * @param ant 天线号 (0x01~0x04)
 * @param start_block 写入块号 (1~15)
 * @param write_data 写入数据
 * @param write_len 写入数据长度，必须是8的倍数
 * @param timeout_ms 超时时间（毫秒）
 * @return 错误码
 */
__declspec(dllexport) int write_low_freq_tag_with_timeout(int handle, unsigned char ant, unsigned char start_block, const unsigned char* write_data, int write_len, int timeout_ms);

/**
 * @brief 写低频标签UID
 * @param handle 连接句柄
 * @param ant 天线号 (0x01~0x04)
 * @param uid_data UID数据
 * @param uid_len UID数据长度
 * @return 错误码
 */
__declspec(dllexport) int write_low_freq_tag_uid(int handle, unsigned char ant, const unsigned char* uid_data, int uid_len);

/**
 * @brief 列出低频标签
 * @param handle 连接句柄
 * @param output_tags 输出标签数组指针
 * @param output_count 输出标签数量
 * @return 错误码，0表示成功
 */
__declspec(dllexport) int list_low_freq_tag(int handle, low_freq_tag** output_tags, int* output_count);
